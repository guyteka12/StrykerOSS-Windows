using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace StrykerOSS.ViewModels;

public partial class ProjectsVm : ObservableObject
{
    private readonly IDbService _db;
    private readonly ISettings _settings;
    private readonly ILogger _log;

    public ObservableCollection<Project> Projects { get; } = new();
    public ObservableCollection<Finding> Findings { get; } = new();
    public ObservableCollection<NoteItem> Notes { get; } = new();
    public ObservableCollection<string> Severities { get; } = new() { "critical", "high", "medium", "low", "info" };

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Prêt";
    [ObservableProperty] private Project? _selectedProject;

    [ObservableProperty] private string _newProjectName = "";
    [ObservableProperty] private string _newProjectClient = "";
    [ObservableProperty] private string _newProjectDescription = "";
    [ObservableProperty] private bool _confirmDelete;

    [ObservableProperty] private string _newFindingTitle = "";
    [ObservableProperty] private string _newFindingSeverity = "medium";
    [ObservableProperty] private string _newFindingDescription = "";
    [ObservableProperty] private string _newFindingImpact = "";
    [ObservableProperty] private string _newFindingPoc = "";
    [ObservableProperty] private string _newFindingRemediation = "";

    [ObservableProperty] private string _newNoteTitle = "";
    [ObservableProperty] private string _newNoteBody = "";

    public ProjectsVm(IDbService db, ISettings settings, ILogger log)
    {
        _db = db;
        _settings = settings;
        _log = log;
        _ = LoadProjectsAsync();
    }

    partial void OnSelectedProjectChanged(Project? value) => _ = LoadProjectAsync();

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // ---------------- loading ----------------

    private async Task LoadProjectsAsync()
    {
        try
        {
            var list = await _db.GetProjectsAsync();
            var selId = SelectedProject?.Id ?? 0;
            await App.Current.Dispatcher.InvokeAsync(() =>
            {
                Projects.Clear();
                foreach (var p in list) Projects.Add(p);
                SelectedProject = selId > 0 ? Projects.FirstOrDefault(x => x.Id == selId) : Projects.FirstOrDefault();
            });
            Status = $"{list.Count} projet(s) enregistré(s)";
        }
        catch (Exception ex)
        {
            _log.Error("projects", "échec du chargement des projets", ex);
            Status = "Erreur : " + ex.Message;
        }
    }

    private async Task LoadProjectAsync()
    {
        var p = SelectedProject;
        if (p is not { Id: > 0 })
        {
            await App.Current.Dispatcher.InvokeAsync(() =>
            {
                Findings.Clear();
                Notes.Clear();
            });
            return;
        }
        try
        {
            var findings = await _db.GetFindingsAsync(p.Id);
            var notes = await _db.GetNotesAsync(p.Id);
            await App.Current.Dispatcher.InvokeAsync(() =>
            {
                Findings.Clear();
                foreach (var f in findings) Findings.Add(f);
                Notes.Clear();
                foreach (var n in notes) Notes.Add(n);
            });
            Status = $"Projet « {p.Name} » : {findings.Count} constat(s), {notes.Count} note(s)";
        }
        catch (Exception ex)
        {
            _log.Error("projects", "échec du chargement du projet", ex);
            Status = "Erreur : " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try { await LoadProjectsAsync(); if (SelectedProject != null) await LoadProjectAsync(); }
        finally { IsBusy = false; }
    }

    // ---------------- projects ----------------

    [RelayCommand]
    private void SelectProject(Project? p)
    {
        if (p is { Id: > 0 }) SelectedProject = p;
    }

    [RelayCommand]
    private async Task CreateProjectAsync()
    {
        if (IsBusy) return;
        if (string.IsNullOrWhiteSpace(NewProjectName)) { Status = "Le nom du projet est requis."; return; }
        IsBusy = true;
        try
        {
            var p = await _db.CreateProjectAsync(NewProjectName.Trim(),
                NullIfEmpty(NewProjectDescription), NewProjectClient.Trim());
            NewProjectName = "";
            NewProjectClient = "";
            NewProjectDescription = "";
            Status = $"Projet #{p.Id} créé : {p.Name}";
            await LoadProjectsAsync();
            SelectedProject = Projects.FirstOrDefault(x => x.Id == p.Id);
            await LoadProjectAsync();
            _log.Info("projects", $"projet #{p.Id} créé");
        }
        catch (Exception ex)
        {
            _log.Error("projects", "échec de la création", ex);
            Status = "Erreur : " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task DeleteProjectAsync()
    {
        if (IsBusy) return;
        var proj = SelectedProject;
        if (proj is not { Id: > 0 }) { Status = "Aucun projet sélectionné."; return; }
        if (!ConfirmDelete) { Status = "Cochez la case de confirmation pour activer la suppression."; return; }
        IsBusy = true;
        try
        {
            await _db.DeleteProjectAsync(proj.Id);
            _log.Warn("projects", $"projet #{proj.Id} « {proj.Name} » supprimé");
            ConfirmDelete = false;
            SelectedProject = null;
            await LoadProjectsAsync();
            Status = $"Projet « {proj.Name} » supprimé.";
        }
        catch (Exception ex)
        {
            _log.Error("projects", "échec de la suppression", ex);
            Status = "Erreur : " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    // ---------------- findings ----------------

    [RelayCommand]
    private async Task AddFindingAsync()
    {
        if (IsBusy) return;
        var proj = SelectedProject;
        if (proj is not { Id: > 0 }) { Status = "Sélectionnez d'abord un projet."; return; }
        if (string.IsNullOrWhiteSpace(NewFindingTitle)) { Status = "Le titre du constat est requis."; return; }
        IsBusy = true;
        try
        {
            var sev = string.IsNullOrWhiteSpace(NewFindingSeverity) ? "info" : NewFindingSeverity.Trim().ToLowerInvariant();
            var f = await _db.AddFindingAsync(new Finding(0, proj.Id, NewFindingTitle.Trim(), sev,
                NullIfEmpty(NewFindingDescription), NullIfEmpty(NewFindingImpact),
                NullIfEmpty(NewFindingPoc), NullIfEmpty(NewFindingRemediation), DateTime.Now));
            _log.Info("projects", $"constat #{f.Id} ajouté au projet {proj.Id}");
            NewFindingTitle = "";
            NewFindingDescription = "";
            NewFindingImpact = "";
            NewFindingPoc = "";
            NewFindingRemediation = "";
            await LoadProjectAsync();
            Status = "Constat ajouté.";
        }
        catch (Exception ex)
        {
            _log.Error("projects", "échec de l'ajout du constat", ex);
            Status = "Erreur : " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task DeleteFindingAsync(Finding? f)
    {
        if (f is null) return;
        try
        {
            await _db.DeleteFindingAsync(f.Id);
            _log.Info("projects", $"constat #{f.Id} supprimé");
            await LoadProjectAsync();
            Status = "Constat supprimé.";
        }
        catch (Exception ex)
        {
            _log.Error("projects", "échec de la suppression du constat", ex);
            Status = "Erreur : " + ex.Message;
        }
    }

    // ---------------- notes ----------------

    [RelayCommand]
    private async Task AddNoteAsync()
    {
        if (IsBusy) return;
        var proj = SelectedProject;
        if (proj is not { Id: > 0 }) { Status = "Sélectionnez d'abord un projet."; return; }
        if (string.IsNullOrWhiteSpace(NewNoteTitle)) { Status = "Le titre de la note est requis."; return; }
        IsBusy = true;
        try
        {
            var n = await _db.AddNoteAsync(new NoteItem(0, proj.Id, NewNoteTitle.Trim(), NewNoteBody ?? "", DateTime.Now));
            _log.Info("projects", $"note #{n.Id} ajoutée au projet {proj.Id}");
            NewNoteTitle = "";
            NewNoteBody = "";
            await LoadProjectAsync();
            Status = "Note ajoutée.";
        }
        catch (Exception ex)
        {
            _log.Error("projects", "échec de l'ajout de la note", ex);
            Status = "Erreur : " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task DeleteNoteAsync(NoteItem? n)
    {
        if (n is null) return;
        try
        {
            await _db.DeleteNoteAsync(n.Id);
            _log.Info("projects", $"note #{n.Id} supprimée");
            await LoadProjectAsync();
            Status = "Note supprimée.";
        }
        catch (Exception ex)
        {
            _log.Error("projects", "échec de la suppression de la note", ex);
            Status = "Erreur : " + ex.Message;
        }
    }

    // ---------------- backup ----------------

    [RelayCommand]
    private async Task ExportBackupAsync()
    {
        if (IsBusy) return;
        var proj = SelectedProject;
        if (proj is not { Id: > 0 }) { Status = "Sélectionnez d'abord un projet."; return; }
        IsBusy = true;
        try
        {
            var outDir = Path.Combine(_settings.EnsureDataDir(), "backups");
            var path = await _db.BackupProjectAsync(proj.Id, outDir);
            Status = "Sauvegarde créée : " + path;
            _log.Info("projects", $"sauvegarde : {path}");
        }
        catch (Exception ex)
        {
            _log.Error("projects", "échec de la sauvegarde", ex);
            Status = "Erreur : " + ex.Message;
        }
        finally { IsBusy = false; }
    }
}
