using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace StrykerOSS.ViewModels;

public partial class DashboardVm : ObservableObject
{
    private readonly IDbService _db;
    private readonly IAdmin _admin;
    private readonly INavigation _nav;
    private readonly ILogger _log;

    public ObservableCollection<ScanRecord> RecentScans { get; } = new();

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Chargement...";
    [ObservableProperty] private string _adminLine = "";
    [ObservableProperty] private bool _isElevated;

    [ObservableProperty] private int _projectsCount;
    [ObservableProperty] private int _scansCount;
    [ObservableProperty] private int _findingsCount;
    [ObservableProperty] private int _criticalCount;
    [ObservableProperty] private int _reportsCount;
    [ObservableProperty] private int _sessionsCount;
    [ObservableProperty] private int _handshakesCount;
    [ObservableProperty] private int _templatesCount;
    [ObservableProperty] private int _geoPointsCount;

    public DashboardVm(IDbService db, IAdmin admin, INavigation nav, ILogger log)
    {
        _db = db;
        _admin = admin;
        _nav = nav;
        _log = log;

        AdminLine = $"$ {_admin.CurrentUser()}@{_admin.MachineName()} - {_admin.WindowsVersion()}";
        IsElevated = admin.IsElevated;

        _ = LoadCoreAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        await LoadCoreAsync();
    }

    private async Task LoadCoreAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var stats = await _db.GetStatsAsync();
            var scans = await _db.GetScansAsync(null, 10);
            App.Current.Dispatcher.Invoke(() =>
            {
                ProjectsCount = stats.Projects;
                ScansCount = stats.Scans;
                FindingsCount = stats.Findings;
                CriticalCount = stats.CriticalFindings;
                ReportsCount = stats.Reports;
                SessionsCount = stats.Sessions;
                HandshakesCount = stats.Handshakes;
                TemplatesCount = stats.Templates;
                GeoPointsCount = stats.GeoPoints;

                RecentScans.Clear();
                foreach (var s in scans) RecentScans.Add(s);
            });
            Status = $"Prêt — {stats.Scans} analyse(s), {stats.Findings} constat(s), {stats.Reports} rapport(s)";
            _log.Info("dashboard", "statistiques + analyses récentes chargées");
        }
        catch (Exception ex)
        {
            _log.Error("dashboard", "échec du chargement", ex);
            Status = "Erreur : " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand] private void OpenWifi() => _nav.GoTo("wifi");
    [RelayCommand] private void OpenLan() => _nav.GoTo("lan");
    [RelayCommand] private void OpenNmap() => _nav.GoTo("nmap");
    [RelayCommand] private void OpenNuclei() => _nav.GoTo("nuclei");
    [RelayCommand] private void OpenProjects() => _nav.GoTo("projects");
}
