using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace StrykerOSS.ViewModels;

public partial class ReportsVm : ObservableObject
{
    private readonly IReportService _reports;
    private readonly IDbService _db;
    private readonly ISettings _settings;
    private readonly ILogger _log;

    public ObservableCollection<Project> Projects { get; } = new();
    public ObservableCollection<ReportRecord> Reports { get; } = new();
    public ObservableCollection<string> Formats { get; } = new() { "PDF", "HTML", "Markdown" };

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Prêt";
    [ObservableProperty] private Project? _selectedProject;
    [ObservableProperty] private string _title = "Rapport de test d'intrusion";
    [ObservableProperty] private string _author = "";
    [ObservableProperty] private string _execSummary = "";
    [ObservableProperty] private string _selectedFormat = "PDF";

    public ReportsVm(IReportService reports, IDbService db, ISettings settings, ILogger log)
    {
        _reports = reports;
        _db = db;
        _settings = settings;
        _log = log;

        Author = settings.Value.DefaultAuthor;
        _ = LoadCoreAsync();
    }

    partial void OnSelectedProjectChanged(Project? value)
    {
        if (value is { Id: > 0 } &&
            (string.IsNullOrWhiteSpace(Title) || Title == "Rapport de test d'intrusion"))
        {
            Title = $"Rapport de test d'intrusion - {value.Name}";
        }
    }

    private async Task LoadCoreAsync()
    {
        var projects = await _db.GetProjectsAsync();
        var reports = await _db.GetReportsAsync(null);
        var selId = SelectedProject?.Id ?? 0;
        await App.Current.Dispatcher.InvokeAsync(() =>
        {
            // sentinel first row: Id 0 = "no project link"
            Projects.Clear();
            Projects.Add(new Project(0, "— aucun projet —", null, "", "", DateTime.MinValue, DateTime.MinValue));
            foreach (var p in projects) Projects.Add(p);
            SelectedProject = selId > 0 ? Projects.FirstOrDefault(x => x.Id == selId) : Projects[0];

            Reports.Clear();
            foreach (var r in reports) Reports.Add(r);
        });
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            await LoadCoreAsync();
            Status = $"{Reports.Count} rapport(s) enregistré(s)";
        }
        catch (Exception ex)
        {
            _log.Error("reports", "échec du rechargement", ex);
            Status = "Erreur : " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task GenerateAsync()
    {
        if (IsBusy) return;
        if (string.IsNullOrWhiteSpace(Title)) { Status = "Le titre du rapport est requis."; return; }
        IsBusy = true;
        try
        {
            var proj = SelectedProject is { Id: > 0 } ? SelectedProject : null;
            var findings = proj != null ? await _db.GetFindingsAsync(proj.Id) : new List<Finding>();
            var outDir = Path.Combine(_settings.EnsureDataDir(), "reports");
            var path = await _reports.GenerateAsync(
                proj, findings, Title.Trim(),
                string.IsNullOrWhiteSpace(Author) ? _settings.Value.DefaultAuthor : Author.Trim(),
                string.IsNullOrWhiteSpace(ExecSummary) ? null : ExecSummary.Trim(),
                SelectedFormat, outDir);
            Status = "Généré : " + path;
            _log.Info("reports", $"rapport généré : {path}");
            await LoadCoreAsync();
        }
        catch (OperationCanceledException)
        {
            Status = "Annulé";
        }
        catch (Exception ex)
        {
            _log.Error("reports", "échec de la génération", ex);
            Status = "Erreur : " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void Open(ReportRecord? r)
    {
        if (r is null || string.IsNullOrWhiteSpace(r.Path)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = r.Path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _log.Error("reports", $"échec de l'ouverture : {r.Path}", ex);
            Status = "Échec de l'ouverture : " + ex.Message;
        }
    }
}
