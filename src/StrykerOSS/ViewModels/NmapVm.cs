using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StrykerOSS.Core.Services;

namespace StrykerOSS.ViewModels;

/// <summary>
/// Nmap module: runs the real nmap.exe with a profile or custom flags, streams live
/// output, parses the real XML result into hosts/ports, exports a dark HTML report
/// and records every scan in the database.
/// </summary>
public partial class NmapVm : ObservableObject
{
    private readonly INmapService _nmap;
    private readonly IDbService _db;
    private readonly ISettings _settings;
    private readonly ILogger _log;

    private NmapRun? _lastRun;

    public IReadOnlyList<NmapProfile> Profiles => _nmap.Profiles;

    public ObservableCollection<string> LogLines { get; } = new();
    public ObservableCollection<NmapHostRow> Hosts { get; } = new();

    [ObservableProperty] private string _target = "";
    [ObservableProperty] private NmapProfile? _selectedProfile;
    [ObservableProperty] private string _customArgs = "-sV -T3";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Ready";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _lastXmlPath = "";
    [ObservableProperty] private string _profileDescription = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPlaceholder))]
    private bool _hasResults;
    [ObservableProperty] private bool _hasXml;

    public bool ShowPlaceholder => !HasResults;

    public NmapVm(INmapService nmap, IDbService db, ISettings settings, ILogger log)
    {
        _nmap = nmap;
        _db = db;
        _settings = settings;
        _log = log;
        SelectedProfile = Profiles.FirstOrDefault(p => p.Key == "default") ?? Profiles.FirstOrDefault();
    }

    partial void OnSelectedProfileChanged(NmapProfile? value)
    {
        if (value == null) return;
        CustomArgs = value.Args;
        ProfileDescription = value.Description;
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task RunAsync(CancellationToken ct)
    {
        if (IsBusy) return;
        if (string.IsNullOrWhiteSpace(Target))
        {
            Status = "Enter a target (IP, range, CIDR or hostname)";
            return;
        }
        if (_nmap.FindNmap() == null)
        {
            Status = "Nmap not found. Install from https://nmap.org or Tools Manager.";
            Line("nmap.exe not found - install it from https://nmap.org or use Tools Manager.");
            return;
        }

        IsBusy = true;
        HasResults = false;
        Ui(() => { Hosts.Clear(); Summary = ""; });

        var targetTrimmed = Target.Trim();
        var sw = Stopwatch.StartNew();
        var started = DateTime.Now;
        var profileName = SelectedProfile?.Name;

        try
        {
            var progress = new Progress<string>(Line);
            var run = await Task.Run(() => _nmap.RunAsync(targetTrimmed, CustomArgs, progress, ct, profileName), ct);
            sw.Stop();

            _lastRun = run;
            var up = run.Hosts.Count(h => h.Up);

            Ui(() =>
            {
                LastXmlPath = run.RawXmlPath ?? "";
                HasXml = File.Exists(LastXmlPath);
                foreach (var h in run.Hosts) Hosts.Add(new NmapHostRow(h));
                Summary = $"{up} host(s) up / {run.Hosts.Count} scanned in {sw.Elapsed.TotalSeconds:0.0}s";
                HasResults = run.Hosts.Count > 0;
                Status = "Done";
            });

            await _db.AddScanAsync(new ScanRecord(0, null, "nmap", targetTrimmed, started,
                (int)sw.Elapsed.TotalMilliseconds, $"{up} hosts up ({profileName ?? "custom"})", run.RawXmlPath));
            _log.Info("nmap", $"scan done: {targetTrimmed} - {up} hosts up");
        }
        catch (OperationCanceledException)
        {
            Ui(() => Status = "Cancelled");
            _log.Warn("nmap", "scan cancelled");
        }
        catch (Exception ex)
        {
            _log.Error("nmap", "scan failed", ex);
            Ui(() => Status = "Error: " + ex.Message);
        }
        finally
        {
            Ui(() => IsBusy = false);
        }
    }

    [RelayCommand]
    private async Task ExportHtmlAsync()
    {
        var run = _lastRun;
        if (run == null)
        {
            Status = "No results to export - run a scan first";
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export nmap report as HTML",
            Filter = "HTML report (*.html)|*.html|All files (*.*)|*.*",
            FileName = $"nmap_report_{DateTime.Now:yyyyMMdd_HHmmss}.html",
            InitialDirectory = _settings.EnsureDataDir(),
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var outDir = Path.GetDirectoryName(dlg.FileName);
            if (string.IsNullOrWhiteSpace(outDir)) outDir = _settings.EnsureDataDir();

            var built = await Task.Run(() => _nmap.ExportHtmlAsync(run, outDir));
            if (!string.Equals(Path.GetFullPath(built), Path.GetFullPath(dlg.FileName), StringComparison.OrdinalIgnoreCase))
                File.Move(built, dlg.FileName, overwrite: true);

            await _db.AddReportAsync(new ReportRecord(0, null, $"Nmap scan - {run.Target}", "HTML",
                dlg.FileName, DateTime.Now, _settings.Value.DefaultAuthor));

            Line($"HTML report saved: {dlg.FileName}");
            Status = "Report exported";
            _log.Info("nmap", $"report exported: {dlg.FileName}");
        }
        catch (Exception ex)
        {
            _log.Error("nmap", "export failed", ex);
            Status = "Export failed: " + ex.Message;
        }
    }

    [RelayCommand]
    private void OpenXml()
    {
        if (string.IsNullOrWhiteSpace(LastXmlPath) || !File.Exists(LastXmlPath))
        {
            Status = "No raw XML available for this run";
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{LastXmlPath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Error("nmap", "opening raw XML failed", ex);
            Status = "Could not open: " + ex.Message;
        }
    }

    private void Line(string s)
    {
        void Add()
        {
            LogLines.Add(s);
            while (LogLines.Count > 2000) LogLines.RemoveAt(0);
        }
        var app = Application.Current;
        if (app == null) return;
        if (app.Dispatcher.CheckAccess()) Add();
        else app.Dispatcher.Invoke(Add);
    }

    private static void Ui(Action action)
    {
        var app = Application.Current;
        if (app == null) return;
        if (app.Dispatcher.CheckAccess()) action();
        else app.Dispatcher.Invoke(action);
    }
}

/// <summary>Display row for a scanned host (immutable snapshot of the run result).</summary>
public sealed class NmapHostRow
{
    public NmapHostRow(NmapHost h)
    {
        Ip = h.Ip;
        Hostname = h.Hostname;
        OsGuess = h.OsGuess;
        Mac = h.Mac;
        MacVendor = h.MacVendor;
        Up = h.Up;
        Ports = h.Ports.Select(p => new NmapPortRow(p)).ToList();
    }

    public string Ip { get; }
    public string? Hostname { get; }
    public string? OsGuess { get; }
    public string? Mac { get; }
    public string? MacVendor { get; }
    public bool Up { get; }
    public List<NmapPortRow> Ports { get; }

    public string HostLabel => string.IsNullOrWhiteSpace(Hostname) ? Ip : $"{Ip}  ({Hostname})";
    public string MacLabel => Mac == null ? "" : string.IsNullOrWhiteSpace(MacVendor) ? Mac : $"{Mac} - {MacVendor}";
    public string OsLabel => OsGuess ?? "";
    public string PortSummary => Ports.Count == 0
        ? "no ports reported"
        : $"{Ports.Count(p => p.State == "open")} open / {Ports.Count} ports reported";
    public bool HasMac => MacLabel.Length > 0;
    public bool HasOs => OsLabel.Length > 0;
}

/// <summary>Display row for a single nmap port entry.</summary>
public sealed class NmapPortRow
{
    public NmapPortRow(NmapPort p)
    {
        Port = p.Port;
        Proto = p.Proto;
        State = p.State;
        Service = p.Service;
        Version = p.Version;
        ScriptOutput = p.ScriptOutput;
    }

    public int Port { get; }
    public string Proto { get; }
    public string State { get; }
    public string Service { get; }
    public string? Version { get; }
    public string? ScriptOutput { get; }

    public string Display => string.IsNullOrWhiteSpace(Version)
        ? $"{Port}/{Proto} {State} {Service}"
        : $"{Port}/{Proto} {State} {Service} {Version}";

    public bool HasScript => !string.IsNullOrWhiteSpace(ScriptOutput);
}
