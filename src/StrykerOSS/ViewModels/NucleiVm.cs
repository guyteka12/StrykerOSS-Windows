using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using StrykerOSS.Core.Services;

namespace StrykerOSS.ViewModels;

/// <summary>
/// Nuclei web scanner module: install/update the real binary, run real scans,
/// live log, findings list, CSV export, raw JSONL access, scan recorded to DB.
/// </summary>
public partial class NucleiVm : ObservableObject
{
    private readonly INucleiService _nuclei;
    private readonly IDbService _db;
    private readonly ILogger _log;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Ready";
    [ObservableProperty] private string _installStatus = "checking...";
    [ObservableProperty] private string _targetsText = "";
    [ObservableProperty] private string _tagsText = "";
    [ObservableProperty] private bool _sevInfo;
    [ObservableProperty] private bool _sevLow;
    [ObservableProperty] private bool _sevMedium = true;
    [ObservableProperty] private bool _sevHigh = true;
    [ObservableProperty] private bool _sevCritical = true;
    [ObservableProperty] private string _summary = "no scan yet";
    [ObservableProperty] private string? _rawPath;

    public ObservableCollection<string> LogLines { get; } = new();
    public ObservableCollection<NucleiFinding> Findings { get; } = new();

    public NucleiVm(INucleiService nuclei, IDbService db, ILogger log)
    {
        _nuclei = nuclei;
        _db = db;
        _log = log;
        _ = RefreshInstallStatusAsync();
    }

    private async Task RefreshInstallStatusAsync()
    {
        try
        {
            var path = await Task.Run(_nuclei.FindNuclei);
            InstallStatus = path ?? "not installed";
        }
        catch (Exception ex)
        {
            InstallStatus = "not installed";
            _log.Warn("nuclei", "locate failed: " + ex.Message);
        }
    }

    private void AddLog(string line) => App.Current.Dispatcher.Invoke(() =>
    {
        LogLines.Add(line);
        while (LogLines.Count > 500) LogLines.RemoveAt(0);
    });

    // ---------------- install ----------------

    [RelayCommand]
    private async Task InstallAsync(CancellationToken ct)
    {
        if (IsBusy) return;
        IsBusy = true;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Status = "Installing nuclei...";
        try
        {
            var prog = new Progress<string>(AddLog);
            var path = await Task.Run(async () => await _nuclei.EnsureInstalledAsync(prog, _cts.Token), _cts.Token);
            InstallStatus = path;
            Status = "nuclei ready: " + path;
            _log.Info("nuclei", "installed/updated: " + path);
        }
        catch (OperationCanceledException) { Status = "Cancelled"; }
        catch (Exception ex) { _log.Error("nuclei", "install failed", ex); Status = "Error: " + ex.Message; }
        finally { IsBusy = false; _cts?.Dispose(); _cts = null; }
    }

    // ---------------- scan ----------------

    [RelayCommand]
    private async Task RunScanAsync(CancellationToken ct)
    {
        if (IsBusy) return;
        var targets = TargetsText
            .Split(new[] { '\r', '\n', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (targets.Count == 0) { Status = "enter at least one target (one host or URL per line)"; return; }

        var sev = new List<string>();
        if (SevCritical) sev.Add("critical");
        if (SevHigh) sev.Add("high");
        if (SevMedium) sev.Add("medium");
        if (SevLow) sev.Add("low");
        if (SevInfo) sev.Add("info");
        var tags = TagsText.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        IsBusy = true;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Status = "nuclei running...";
        Summary = "scanning...";
        App.Current.Dispatcher.Invoke(() => { LogLines.Clear(); Findings.Clear(); });
        try
        {
            var prog = new Progress<string>(AddLog);
            AddLog("note: first run downloads the nuclei template database (one time)");
            var run = await Task.Run(async () => await _nuclei.ScanAsync(targets, sev, tags, prog, _cts.Token), _cts.Token);

            App.Current.Dispatcher.Invoke(() =>
            {
                foreach (var f in run.Findings) Findings.Add(f);
            });
            RawPath = run.RawPath;

            int c = run.Findings.Count(f => f.Severity == "CRITICAL");
            int h = run.Findings.Count(f => f.Severity == "HIGH");
            int m = run.Findings.Count(f => f.Severity == "MEDIUM");
            int l = run.Findings.Count(f => f.Severity == "LOW");
            int i = run.Findings.Count(f => f.Severity == "INFO");
            Summary = $"{run.Findings.Count} findings - critical {c}, high {h}, medium {m}, low {l}, info {i}";
            Status = $"done in {run.Duration.TotalSeconds:0.0}s";

            try
            {
                await _db.AddScanAsync(new ScanRecord(0, null, "nuclei", run.Targets, run.Started,
                    (int)run.Duration.TotalMilliseconds, Summary, run.RawPath));
                _log.Info("nuclei", "scan recorded to project database");
            }
            catch (Exception dbEx)
            {
                _log.Warn("nuclei", "could not record scan: " + dbEx.Message);
            }
        }
        catch (OperationCanceledException) { Status = "Cancelled"; Summary = "cancelled"; }
        catch (Exception ex) { _log.Error("nuclei", "scan failed", ex); Status = "Error: " + ex.Message; }
        finally { IsBusy = false; _cts?.Dispose(); _cts = null; }
    }

    [RelayCommand]
    private void CancelScan()
    {
        if (_cts is null) return;
        _cts.Cancel();
        Status = "cancelling...";
    }

    // ---------------- export ----------------

    [RelayCommand]
    private void ExportCsv()
    {
        if (Findings.Count == 0) { Status = "nothing to export - run a scan first"; return; }
        var dlg = new SaveFileDialog
        {
            Title = "Export nuclei findings (CSV)",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            FileName = $"nuclei_findings_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("template_id,name,severity,host,matched_at,extracted,description,reference,found_at");
            foreach (var f in Findings)
            {
                sb.AppendLine(string.Join(',',
                    Csv(f.TemplateId), Csv(f.Name), Csv(f.Severity), Csv(f.Host), Csv(f.MatchedAt),
                    Csv(f.Extracted ?? ""), Csv(f.Description ?? ""), Csv(f.Reference ?? ""),
                    Csv(f.FoundAt.ToString("yyyy-MM-dd HH:mm:ss"))));
            }
            File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(false));
            Status = "exported: " + dlg.FileName;
            _log.Info("nuclei", $"exported {Findings.Count} findings -> {dlg.FileName}");
        }
        catch (Exception ex) { _log.Error("nuclei", "csv export failed", ex); Status = "Error: " + ex.Message; }
    }

    private static string Csv(string s)
        => s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r')
            ? "\"" + s.Replace("\"", "\"\"") + "\""
            : s;

    [RelayCommand]
    private void OpenRaw()
    {
        if (RawPath is null || !File.Exists(RawPath)) { Status = "no raw JSONL yet - run a scan first"; return; }
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{RawPath}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { _log.Error("nuclei", "open raw failed", ex); Status = "Error: " + ex.Message; }
    }
}
