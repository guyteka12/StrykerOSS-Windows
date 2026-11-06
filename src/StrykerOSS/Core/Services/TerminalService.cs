using System.Diagnostics;
using System.Text;

namespace StrykerOSS.Core.Services;

/// <summary>
/// Real terminal host: spawns an actual powershell.exe / cmd.exe / wsl.exe process
/// with fully redirected stdin/stdout/stderr. This app runs elevated, so the
/// spawned shells are elevated too.
/// </summary>
public sealed class TerminalService : ITerminalService
{
    private readonly ILogger _log;

    public TerminalService(ILogger log) => _log = log;

    public async Task<ITerminalSession> StartAsync(string shell, string? cwd, IProgress<string>? output,
        CancellationToken ct = default)
    {
        var key = (shell ?? "powershell").Trim().ToLowerInvariant();
        string fileName;
        string arguments;

        switch (key)
        {
            case "powershell":
                fileName = "powershell.exe";
                arguments = "-NoProfile -ExecutionPolicy Bypass -Command -";
                break;

            case "cmd":
                fileName = "cmd.exe";
                arguments = "/Q /K";
                break;

            case "wsl":
                if (!await Task.Run(() => WslAvailable()))
                {
                    _log.Warn("terminal", "wsl requested but not installed");
                    throw new InvalidOperationException(
                        "WSL est introuvable sur cette machine. Installez-le depuis une invite élevée avec : wsl --install " +
                        "(redémarrage requis) - https://learn.microsoft.com/windows/wsl/install");
                }
                fileName = "wsl.exe";
                arguments = string.Empty;
                break;

            default:
                throw new InvalidOperationException($"Shell inconnu '{shell}'. Shells pris en charge : powershell, cmd, wsl.");
        }

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = string.IsNullOrWhiteSpace(cwd) ? Environment.SystemDirectory : cwd
        };

        ct.ThrowIfCancellationRequested();

        var p = Process.Start(psi) ?? throw new InvalidOperationException($"Échec du démarrage de {fileName}.");
        var session = new TerminalSession(p, output);
        session.BeginReads();

        _log.Info("terminal", $"{key} session started (pid {p.Id}, cwd {psi.WorkingDirectory})");
        output?.Report($"[session {key} démarrée - pid {p.Id}]");
        return session;
    }

    /// <summary>Detects WSL with a fast real `where wsl` lookup (no System.Management).</summary>
    private static bool WslAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", "/c where wsl")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            var first = p.StandardOutput.ReadLine();
            p.WaitForExit(4000);
            if (!p.HasExited) { try { p.Kill(true); } catch { } }
            return !string.IsNullOrWhiteSpace(first);
        }
        catch { return false; }
    }
}

/// <summary>One live shell process with interactive stdin and streamed output.</summary>
internal sealed class TerminalSession : ITerminalSession
{
    private readonly Process _p;
    private readonly IProgress<string>? _output;

    public event Action? Exited;

    public TerminalSession(Process p, IProgress<string>? output)
    {
        _p = p;
        _output = output;
        _p.EnableRaisingEvents = true;
        _p.Exited += OnProcessExited;
    }

    public bool IsAlive
    {
        get { try { return !_p.HasExited; } catch { return false; } }
    }

    public int? Pid
    {
        get { try { return _p.Id; } catch { return null; } }
    }

    public void BeginReads()
    {
        _p.OutputDataReceived += (_, e) => { if (e.Data != null) _output?.Report(e.Data); };
        _p.ErrorDataReceived += (_, e) => { if (e.Data != null) _output?.Report("[err] " + e.Data); };
        _p.BeginOutputReadLine();
        _p.BeginErrorReadLine();
    }

    public void WriteLine(string line)
    {
        if (!IsAlive) return;
        _output?.Report($"> {line}");
        try
        {
            _p.StandardInput.WriteLine(line);
            _p.StandardInput.Flush();
        }
        catch (Exception ex)
        {
            _output?.Report($"[échec d'écriture : {ex.Message}]");
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        _output?.Report($"[session terminée - pid {_p.Id}]");
        try { Exited?.Invoke(); } catch { /* subscriber faults must not kill the notifier */ }
    }

    public async ValueTask DisposeAsync()
    {
        try { if (!_p.HasExited) _p.Kill(true); } catch { }
        try { _p.Dispose(); } catch { }
        await Task.CompletedTask;
    }
}
