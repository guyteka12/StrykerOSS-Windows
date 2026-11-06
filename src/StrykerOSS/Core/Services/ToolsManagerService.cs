using System.Diagnostics;

namespace StrykerOSS.Core.Services;

/// <summary>
/// Tools manager: real detection (path + version probe) and real installation
/// via winget (nmap, hashcat) or the nuclei auto-installer. Metasploit gets
/// honest manual guidance because it has no reliable winget package.
/// </summary>
public sealed class ToolsManagerService : IToolsManager
{
    private readonly IToolLocator _tools;
    private readonly INucleiService _nuclei;
    private readonly ISettings _settings;
    private readonly ILogger _log;

    public ToolsManagerService(IToolLocator tools, INucleiService nuclei, ISettings settings, ILogger log)
    {
        _tools = tools;
        _nuclei = nuclei;
        _settings = settings;
        _log = log;
    }

    private static readonly (string Key, string Name, string Hint, string VersionArg)[] Catalog =
    {
        ("nmap",     "Nmap",                "winget install Insecure.Nmap — or https://nmap.org/download",   "--version"),
        ("nuclei",   "Nuclei",              "Auto-download from projectdiscovery releases",                  "-version"),
        ("msfvenom", "Metasploit Framework", "https://www.metasploit.com/download or WSL",                   "--version"),
        ("hashcat",  "Hashcat",             "winget install hashcat.hashcat — or https://hashcat.net/hashcat/", "--version"),
    };

    public async Task<List<ToolStatus>> StatusAsync()
    {
        var list = new List<ToolStatus>();
        foreach (var (key, name, hint, versionArg) in Catalog)
        {
            var path = _tools.ResolveTool(key);
            var version = path != null ? await GetFirstLineAsync(path, versionArg) : null;
            list.Add(new ToolStatus(key, name, path, Found: path != null, Version: version, InstallHint: hint));
        }
        _log.Info("tools", $"status: {list.Count(t => t.Found)}/{list.Count} tools found");
        return list;
    }

    public async Task<bool> InstallAsync(string key, IProgress<string>? log = null, CancellationToken ct = default)
    {
        var k = (key ?? string.Empty).Trim().ToLowerInvariant();
        _log.Info("tools", $"install requested: {k}");

        switch (k)
        {
            case "nmap":
                return await WingetInstallAsync("Insecure.Nmap", log, ct);

            case "nuclei":
                log?.Report("Nuclei: downloading latest release from projectdiscovery GitHub ...");
                var nucleiPath = await _nuclei.EnsureInstalledAsync(log, ct);
                var ok = !string.IsNullOrWhiteSpace(nucleiPath) && File.Exists(nucleiPath);
                log?.Report(ok ? $"Nuclei ready: {nucleiPath}" : "Nuclei install failed.");
                return ok;

            case "hashcat":
                return await WingetInstallAsync("hashcat.hashcat", log, ct);

            case "msfvenom":
                log?.Report("Metasploit Framework cannot be installed automatically on Windows.");
                log?.Report("1) Download the Windows installer: https://www.metasploit.com/download");
                log?.Report("2) Run it, then point Settings -> MsfPath at msfvenom.bat / msfconsole.bat.");
                log?.Report("3) Start the RPC daemon once:  msfrpcd -P <your-password> -S -p 55553 -a 127.0.0.1");
                log?.Report("   (add -u <user> if you want a named user; keep -S to disable SSL self-signed prompts)");
                log?.Report("Alternative: install Kali in WSL and run msfvenom from there.");
                return false;

            default:
                log?.Report($"Unknown tool key '{key}'. Known keys: nmap, nuclei, hashcat, msfvenom.");
                return false;
        }
    }

    public string ToolsDir()
    {
        try { return _tools.ToolsDir(); }
        catch { return _settings.Value.ToolsDir; }
    }

    // ---------------------------------------------------------- helpers

    private async Task<bool> WingetInstallAsync(string packageId, IProgress<string>? log, CancellationToken ct)
    {
        log?.Report($"Running: winget install --id {packageId} -e --accept-source-agreements --accept-package-agreements");

        var psi = new ProcessStartInfo
        {
            FileName = "winget.exe",
            Arguments = $"install --id {packageId} -e --accept-source-agreements --accept-package-agreements",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var p = Process.Start(psi);
        if (p == null)
        {
            log?.Report("winget.exe was not found - install 'App Installer' from the Microsoft Store first.");
            _log.Warn("tools", $"winget missing, cannot install {packageId}");
            return false;
        }

        p.OutputDataReceived += (_, e) => { if (e.Data != null) log?.Report(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) log?.Report(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(10)); // 600s hard timeout
        try
        {
            await p.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { if (!p.HasExited) p.Kill(true); } catch { }
            var cancelledByUser = ct.IsCancellationRequested;
            log?.Report(cancelledByUser ? "Install cancelled." : "winget timed out after 600s - install aborted.");
            _log.Warn("tools", $"winget {packageId}: {(cancelledByUser ? "cancelled" : "timeout")}");
            return false;
        }

        var code = p.ExitCode;
        log?.Report(code == 0
            ? $"winget: '{packageId}' installed successfully. Restart the app or click Refresh to re-detect."
            : $"winget exited with code {code} for '{packageId}'.");
        _log.Info("tools", $"winget {packageId} -> exit {code}");
        return code == 0;
    }

    /// <summary>Runs `exe versionArg` with a 5s timeout and returns the first output line, if any.</summary>
    private static async Task<string?> GetFirstLineAsync(string exe, string versionArg)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = versionArg,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return null;

            var first = await p.StandardOutput.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(first))
                first = await p.StandardError.ReadLineAsync();

            if (!p.WaitForExit(5000))
            {
                try { p.Kill(true); } catch { }
            }
            return string.IsNullOrWhiteSpace(first) ? null : first.Trim();
        }
        catch
        {
            return null; // batch wrappers or broken binaries -> treat as unknown version
        }
    }
}
