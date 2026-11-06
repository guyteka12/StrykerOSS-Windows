using System.Security.Principal;

namespace StrykerOSS.Core.Services;

public sealed class AdminService : IAdmin
{
    public bool IsElevated { get; }

    public AdminService()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            IsElevated = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { IsElevated = false; }
    }

    public string WindowsVersion()
    {
        try
        {
            var v = Environment.OSVersion.Version;
            var name = v.Build >= 22000 ? "Windows 11" : "Windows 10";
            return $"{name} {v.Major}.{v.Minor} (build {v.Build})";
        }
        catch { return Environment.OSVersion.ToString(); }
    }

    public string MachineName() => Environment.MachineName;
    public string CurrentUser() => Environment.UserName;
}

public sealed class SettingsService : ISettings
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StrykerOSS");

    private static readonly string File = Path.Combine(Dir, "settings.json");

    public AppSettings Value { get; private set; } = new();

    public SettingsService()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            if (System.IO.File.Exists(File))
            {
                var json = System.IO.File.ReadAllText(File);
                var loaded = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded != null) Value = loaded;
            }
        }
        catch { /* fresh settings */ }

        if (string.IsNullOrWhiteSpace(Value.DataDir))
        {
            Value.DataDir = Dir;
            Value.ToolsDir = Path.Combine(Dir, "tools");
        }
        Save();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var json = System.Text.Json.JsonSerializer.Serialize(Value,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            System.IO.File.WriteAllText(File, json);
        }
        catch { /* best effort */ }
    }

    public string EnsureDataDir()
    {
        Directory.CreateDirectory(Value.DataDir);
        Directory.CreateDirectory(Value.ToolsDir);
        Directory.CreateDirectory(Path.Combine(Value.DataDir, "reports"));
        Directory.CreateDirectory(Path.Combine(Value.DataDir, "scans"));
        Directory.CreateDirectory(Path.Combine(Value.DataDir, "captures"));
        Directory.CreateDirectory(Path.Combine(Value.DataDir, "payloads"));
        Directory.CreateDirectory(Path.Combine(Value.DataDir, "exports"));
        return Value.DataDir;
    }
}

public sealed class LogService : ILogger
{
    private readonly object _lock = new();
    private readonly List<LogEntry> _mem = new();
    private int _seq;
    private readonly string _logFile;

    public event Action<LogEntry>? Logged;

    public LogService(ISettings settings)
    {
        var dir = settings.EnsureDataDir();
        _logFile = Path.Combine(dir, "logs.txt");
    }

    public void Info(string source, string message) => Add("INFO", source, message, null);
    public void Warn(string source, string message) => Add("WARN", source, message, null);
    public void Error(string source, string message, Exception? ex = null)
        => Add("ERROR", source, ex == null ? message : $"{message} :: {ex.Message}", ex);

    private void Add(string level, string source, string message, Exception? _)
    {
        var e = new LogEntry(Interlocked.Increment(ref _seq), DateTime.Now, level, source, message);
        lock (_lock)
        {
            _mem.Add(e);
            if (_mem.Count > 5000) _mem.RemoveRange(0, 1000);
        }
        try { System.IO.File.AppendAllText(_logFile, $"{e.Time:yyyy-MM-dd HH:mm:ss} [{e.Level}] {e.Source}: {e.Message}{Environment.NewLine}"); } catch { }
        try { Logged?.Invoke(e); } catch { }
    }

    public IReadOnlyList<LogEntry> Recent(int count)
    {
        lock (_lock)
        {
            return _mem.Skip(Math.Max(0, _mem.Count - count)).ToList();
        }
    }
}

public sealed class Navigation : INavigation
{
    public string CurrentKey { get; private set; } = "dashboard";
    public event Action<string>? Navigated;
    public void GoTo(string key)
    {
        if (key == CurrentKey) return;
        CurrentKey = key;
        Navigated?.Invoke(key);
    }
    public void Reset(string key) { CurrentKey = key; }
}

public sealed class ToolLocator : IToolLocator
{
    private readonly ISettings _settings;
    public ToolLocator(ISettings settings) => _settings = settings;

    public string ToolsDir()
    {
        var d = _settings.Value.ToolsDir;
        Directory.CreateDirectory(d);
        return d;
    }

    public string? Find(params string[] exeNames)
    {
        // 1. explicit settings
        foreach (var n in exeNames)
        {
            if (n.Equals("nmap", StringComparison.OrdinalIgnoreCase) && _settings.Value.NmapPath is { Length: > 0 } np && System.IO.File.Exists(np)) return np;
            if (n.Equals("nuclei", StringComparison.OrdinalIgnoreCase) && _settings.Value.NucleiPath is { Length: > 0 } nu && System.IO.File.Exists(nu)) return nu;
            if ((n.Equals("msfconsole", StringComparison.OrdinalIgnoreCase) || n.Equals("msfvenom", StringComparison.OrdinalIgnoreCase))
                && _settings.Value.MsfPath is { Length: > 0 } mp && System.IO.File.Exists(mp)) return mp;
        }
        // 2. tools dir
        var tools = ToolsDir();
        foreach (var n in exeNames)
        {
            var p = Path.Combine(tools, n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n : n + ".exe");
            if (System.IO.File.Exists(p)) return p;
        }
        // 3. PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var n in exeNames)
            {
                try
                {
                    var p = Path.Combine(dir, n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n : n + ".exe");
                    if (System.IO.File.Exists(p)) return p;
                }
                catch { }
            }
        }
        // 4. common install locations
        string[][] roots =
        {
            new[] { @"C:\Program Files\Nmap", "nmap.exe" },
            new[] { @"C:\Program Files (x86)\Nmap", "nmap.exe" },
            new[] { @"C:\Program Files\Metasploit", "bin\\msfconsole.bat" },
            new[] { @"C:\metasploit-framework\bin", "msfconsole.bat" },
        };
        foreach (var r in roots)
        {
            try
            {
                var p = Path.Combine(r[0], r[1]);
                if (System.IO.File.Exists(p)) return p;
            }
            catch { }
        }
        // metasploit via msfconsole bat anywhere in PATH handled above; also try "where"
        foreach (var n in exeNames)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c where {n}")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };
                using var pr = System.Diagnostics.Process.Start(psi);
                if (pr != null)
                {
                    var line = pr.StandardOutput.ReadLine();
                    pr.WaitForExit(3000);
                    if (!string.IsNullOrWhiteSpace(line) && System.IO.File.Exists(line.Trim())) return line.Trim();
                }
            }
            catch { }
        }
        return null;
    }

    public string? ResolveTool(string key) => key.ToLowerInvariant() switch
    {
        "nmap" => Find("nmap"),
        "nuclei" => Find("nuclei"),
        "msfconsole" => Find("msfconsole.bat", "msfconsole"),
        "msfvenom" => Find("msfvenom.bat", "msfvenom"),
        "hashcat" => Find("hashcat"),
        _ => null
    };
}
