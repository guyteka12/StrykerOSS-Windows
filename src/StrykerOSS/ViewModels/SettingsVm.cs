using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace StrykerOSS.ViewModels;

/// <summary>
/// Settings VM: edits a local copy of AppSettings, real auto-detection via
/// IToolLocator, real Save into settings.json, live system info from IAdmin.
/// </summary>
public partial class SettingsVm : ObservableObject
{
    private readonly ISettings _settings;
    private readonly IToolLocator _tools;
    private readonly IAdmin _admin;
    private readonly ILogger _log;

    // ---- editable copy (written back on Save) ----
    [ObservableProperty] private string _nmapPath = "";
    [ObservableProperty] private string _nucleiPath = "";
    [ObservableProperty] private string _msfPath = "";
    [ObservableProperty] private string _msfRpcHost = "127.0.0.1";
    [ObservableProperty] private int _msfRpcPort = 55553;
    [ObservableProperty] private string _msfRpcUser = "msf";
    [ObservableProperty] private string _msfRpcPass = "";
    [ObservableProperty] private string _defaultAuthor = "StrykerOSS Operator";

    // ---- read-only info ----
    [ObservableProperty] private string _status = "Ready";
    [ObservableProperty] private bool _isElevated;
    [ObservableProperty] private string _windowsVersion = "";
    [ObservableProperty] private string _machineName = "";
    [ObservableProperty] private string _currentUser = "";
    [ObservableProperty] private string _dataDir = "";
    [ObservableProperty] private string _toolsDir = "";

    public SettingsVm(ISettings settings, IToolLocator tools, IAdmin admin, ILogger log)
    {
        _settings = settings;
        _tools = tools;
        _admin = admin;
        _log = log;

        var v = settings.Value;
        NmapPath = v.NmapPath ?? "";
        NucleiPath = v.NucleiPath ?? "";
        MsfPath = v.MsfPath ?? "";
        MsfRpcHost = v.MsfRpcHost;
        MsfRpcPort = v.MsfRpcPort;
        MsfRpcUser = v.MsfRpcUser;
        MsfRpcPass = v.MsfRpcPass;
        DefaultAuthor = v.DefaultAuthor;

        IsElevated = admin.IsElevated;
        WindowsVersion = admin.WindowsVersion();
        MachineName = admin.MachineName();
        CurrentUser = admin.CurrentUser();
        DataDir = v.DataDir;
        ToolsDir = v.ToolsDir;
    }

    // ---------------------------------------------------------- paths

    [RelayCommand]
    private void AutoDetect()
    {
        Status = "Auto-detecting tool paths ...";
        var nmap = _tools.ResolveTool("nmap");
        var nuclei = _tools.ResolveTool("nuclei");
        var msf = _tools.ResolveTool("msfvenom") ?? _tools.ResolveTool("msfconsole");

        if (nmap != null) NmapPath = nmap;
        if (nuclei != null) NucleiPath = nuclei;
        if (msf != null) MsfPath = msf;

        Status = $"Auto-detect done - nmap: {nmap ?? "not found"} · nuclei: {nuclei ?? "not found"} · msf: {msf ?? "not found"}";
        _log.Info("settings", $"auto-detect: nmap={nmap ?? "-"} nuclei={nuclei ?? "-"} msf={msf ?? "-"}");
    }

    [RelayCommand]
    private void BrowsePath(string? which)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select executable",
            Filter = "Executables (*.exe)|*.exe|Batch files (*.bat;*.cmd)|*.bat;*.cmd|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dlg.ShowDialog() != true) return;

        switch ((which ?? "").ToLowerInvariant())
        {
            case "nmap": NmapPath = dlg.FileName; break;
            case "nuclei": NucleiPath = dlg.FileName; break;
            case "msf": MsfPath = dlg.FileName; break;
            default: Status = "Unknown browse target: " + which; break;
        }
    }

    // ---------------------------------------------------------- save / folders

    [RelayCommand]
    private void Save()
    {
        try
        {
            var v = _settings.Value;
            v.NmapPath = NullIfEmpty(NmapPath);
            v.NucleiPath = NullIfEmpty(NucleiPath);
            v.MsfPath = NullIfEmpty(MsfPath);
            v.MsfRpcHost = string.IsNullOrWhiteSpace(MsfRpcHost) ? "127.0.0.1" : MsfRpcHost.Trim();
            v.MsfRpcPort = MsfRpcPort > 0 && MsfRpcPort <= 65535 ? MsfRpcPort : 55553;
            v.MsfRpcUser = string.IsNullOrWhiteSpace(MsfRpcUser) ? "msf" : MsfRpcUser.Trim();
            v.MsfRpcPass = MsfRpcPass ?? "";
            v.DefaultAuthor = string.IsNullOrWhiteSpace(DefaultAuthor) ? "StrykerOSS Operator" : DefaultAuthor.Trim();

            _settings.Save();
            Status = "Saved";
            _log.Info("settings", "settings saved to settings.json");
        }
        catch (Exception ex)
        {
            _log.Error("settings", "save failed", ex);
            Status = "Error: " + ex.Message;
        }
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        try
        {
            var dir = _settings.Value.DataDir;
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{dir}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _log.Error("settings", "open data folder failed", ex);
            Status = "Error: " + ex.Message;
        }
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
