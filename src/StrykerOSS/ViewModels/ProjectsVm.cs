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
    [ObservableProperty] private string _status = "Ready";
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
            Status = $"{list.Count} project(s) on record";
        }
        catch (Exception ex)
        {
            _log.Error("projects", "load projects failed", ex);
            Status = "Error: " + ex.Message;
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
            Status = $"Project '{p.Name}': {findings.Count} finding(s), {notes.Count} note(s)";
        }
        catch (Exception ex)
        {
            _log.Error("projects", "load project failed", ex);
            Status = "Error: " + ex.Message;
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
        if (string.IsNullOrWhiteSpace(NewProjectName)) { Status = "Project name is required."; return; }
        IsBusy = true;
        try
        {
            var p = await _db.CreateProjectAsync(NewProjectName.Trim(),
                NullIfEmpty(NewProjectDescription), NewProjectClient.Trim());
            NewProjectName = "";
            NewProjectClient = "";
            NewProjectDescription = "";
            Status = $"Created project #{p.Id}: {p.Name}";
            await LoadProjectsAsync();
            SelectedProject = Projects.FirstOrDefault(x => x.Id == p.Id);
            await LoadProjectAsync();
            _log.Info("projects", $"created project #{p.Id}");
        }
        catch (Exception ex)
        {
            _log.Error("projects", "create failed", ex);
            Status = "Error: " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task DeleteProjectAsync()
    {
        if (IsBusy) return;
        var proj = SelectedProject;
        if (proj is not { Id: > 0 }) { Status = "No project selected."; return; }
        if (!ConfirmDelete) { Status = "Tick the confirm box to enable deletion."; return; }
        IsBusy = true;
        try
        {
            await _db.DeleteProjectAsync(proj.Id);
            _log.Warn("projects", $"project #{proj.Id} '{proj.Name}' deleted");
            ConfirmDelete = false;
            SelectedProject = null;
            await LoadProjectsAsync();
            Status = $"Project '{proj.Name}' deleted.";
        }
        catch (Exception ex)
        {
            _log.Error("projects", "delete failed", ex);
            Status = "Error: " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    // ---------------- findings ----------------

    [RelayCommand]
    private async Task AddFindingAsync()
    {
        if (IsBusy) return;
        var proj = SelectedProject;
        if (proj is not { Id: > 0 }) { Status = "Select a project first."; return; }
        if (string.IsNullOrWhiteSpace(NewFindingTitle)) { Status = "Finding title is required."; return; }
        IsBusy = true;
        try
        {
            var sev = string.IsNullOrWhiteSpace(NewFindingSeverity) ? "info" : NewFindingSeverity.Trim().ToLowerInvariant();
            var f = await _db.AddFindingAsync(new Finding(0, proj.Id, NewFindingTitle.Trim(), sev,
                NullIfEmpty(NewFindingDescription), NullIfEmpty(NewFindingImpact),
                NullIfEmpty(NewFindingPoc), NullIfEmpty(NewFindingRemediation), DateTime.Now));
            _log.Info("projects", $"finding #{f.Id} added to project {proj.Id}");
            NewFindingTitle = "";
            NewFindingDescription = "";
            NewFindingImpact = "";
            NewFindingPoc = "";
            NewFindingRemediation = "";
            await LoadProjectAsync();
            Status = "Finding added.";
        }
        catch (Exception ex)
        {
            _log.Error("projects", "add finding failed", ex);
            Status = "Error: " + ex.Message;
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
            _log.Info("projects", $"finding #{f.Id} deleted");
            await LoadProjectAsync();
            Status = "Finding deleted.";
        }
        catch (Exception ex)
        {
            _log.Error("projects", "delete finding failed", ex);
            Status = "Error: " + ex.Message;
        }
    }

    // ---------------- notes ----------------

    [RelayCommand]
    private async Task AddNoteAsync()
    {
        if (IsBusy) return;
        var proj = SelectedProject;
        if (proj is not { Id: > 0 }) { Status = "Select a project first."; return; }
        if (string.IsNullOrWhiteSpace(NewNoteTitle)) { Status = "Note title is required."; return; }
        IsBusy = true;
        try
        {
            var n = await _db.AddNoteAsync(new NoteItem(0, proj.Id, NewNoteTitle.Trim(), NewNoteBody ?? "", DateTime.Now));
            _log.Info("projects", $"note #{n.Id} added to project {proj.Id}");
            NewNoteTitle = "";
            NewNoteBody = "";
            await LoadProjectAsync();
            Status = "Note added.";
        }
        catch (Exception ex)
        {
            _log.Error("projects", "add note failed", ex);
            Status = "Error: " + ex.Message;
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
            _log.Info("projects", $"note #{n.Id} deleted");
            await LoadProjectAsync();
            Status = "Note deleted.";
        }
        catch (Exception ex)
        {
            _log.Error("projects", "delete note failed", ex);
            Status = "Error: " + ex.Message;
        }
    }

    // ---------------- backup ----------------

    [RelayCommand]
    private async Task ExportBackupAsync()
    {
        if (IsBusy) return;
        var proj = SelectedProject;
        if (proj is not { Id: > 0 }) { Status = "Select a project first."; return; }
        IsBusy = true;
        try
        {
            var outDir = Path.Combine(_settings.EnsureDataDir(), "backups");
            var path = await _db.BackupProjectAsync(proj.Id, outDir);
            Status = "Backup written: " + path;
            _log.Info("projects", $"backup: {path}");
        }
        catch (Exception ex)
        {
            _log.Error("projects", "backup failed", ex);
            Status = "Error: " + ex.Message;
        }
        finally { IsBusy = false; }
    }
}
