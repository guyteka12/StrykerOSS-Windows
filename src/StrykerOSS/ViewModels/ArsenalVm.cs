using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StrykerOSS.Core.Services;

namespace StrykerOSS.ViewModels;

/// <summary>
/// Arsenal module: custom command templates (CRUD via project DB),
/// live render preview for IP/PORT/MAC/GW/MASK inputs, real execution.
/// </summary>
public partial class ArsenalVm : ObservableObject
{
    private readonly IArsenalService _arsenal;
    private readonly ILogger _log;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Prêt";
    [ObservableProperty] private ArsenalTemplate? _selectedTemplate;

    // editor fields
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private string _editCategory = "reconnaissance";
    [ObservableProperty] private string _editCommand = "";
    [ObservableProperty] private string _editDescription = "";

    // execution inputs
    [ObservableProperty] private string _exeIp = "";
    [ObservableProperty] private string _exePort = "";
    [ObservableProperty] private string _exeMac = "";
    [ObservableProperty] private string _exeGw = "";
    [ObservableProperty] private string _exeMask = "";
    [ObservableProperty] private string _preview = "";

    public ObservableCollection<ArsenalTemplate> Templates { get; } = new();
    public ObservableCollection<string> Output { get; } = new();

    public ArsenalVm(IArsenalService arsenal, ILogger log)
    {
        _arsenal = arsenal;
        _log = log;
        _ = LoadAsync(seed: true);
    }

    private async Task LoadAsync(bool seed)
    {
        try
        {
            if (seed) await Task.Run(async () => await _arsenal.SeedIfEmptyAsync());
            var list = await Task.Run(async () => await _arsenal.ListAsync());
            App.Current.Dispatcher.Invoke(() =>
            {
                Templates.Clear();
                foreach (var t in list) Templates.Add(t);
                if (SelectedTemplate is null && Templates.Count > 0) SelectedTemplate = Templates[0];
            });
            Status = $"{list.Count} modèles";
        }
        catch (Exception ex)
        {
            _log.Error("arsenal", "Échec du chargement", ex);
            Status = "Erreur : " + ex.Message;
        }
    }

    private void AddOutput(string line) => App.Current.Dispatcher.Invoke(() =>
    {
        Output.Add(line);
        while (Output.Count > 800) Output.RemoveAt(0);
    });

    // ---------------- selection / editor ----------------

    partial void OnSelectedTemplateChanged(ArsenalTemplate? value)
    {
        if (value is null) return;
        EditName = value.Name;
        EditCategory = value.Category;
        EditCommand = value.Command;
        EditDescription = value.Description;
        UpdatePreview();
    }

    partial void OnExeIpChanged(string value) => UpdatePreview();
    partial void OnExePortChanged(string value) => UpdatePreview();
    partial void OnExeMacChanged(string value) => UpdatePreview();
    partial void OnExeGwChanged(string value) => UpdatePreview();
    partial void OnExeMaskChanged(string value) => UpdatePreview();

    private void UpdatePreview()
    {
        if (SelectedTemplate is null) { Preview = ""; return; }
        try { Preview = _arsenal.Render(SelectedTemplate, ExeIp, ExePort, ExeMac, ExeGw, ExeMask); }
        catch { Preview = SelectedTemplate.Command; }
    }

    [RelayCommand]
    private void NewTemplate()
    {
        SelectedTemplate = null;
        EditName = "Nouveau modèle";
        EditCategory = "reconnaissance";
        EditCommand = "";
        EditDescription = "";
        Status = "nouveau brouillon - remplissez les champs puis Enregistrer (inséré comme nouveau)";
    }

    [RelayCommand]
    private void DuplicateTemplate()
    {
        if (SelectedTemplate is not { } sel) { Status = "sélectionnez d'abord un modèle"; return; }
        EditName = sel.Name + " (copie)";
        EditCategory = sel.Category;
        EditCommand = sel.Command;
        EditDescription = sel.Description;
        SelectedTemplate = null; // clears list selection; draft keeps copied fields
        Status = "copié - ajustez puis Enregistrer (inséré comme nouveau)";
    }

    [RelayCommand]
    private async Task SaveTemplateAsync(CancellationToken ct)
    {
        if (IsBusy) return;
        if (string.IsNullOrWhiteSpace(EditName) || string.IsNullOrWhiteSpace(EditCommand))
        {
            Status = "le nom et la commande sont obligatoires";
            return;
        }
        IsBusy = true;
        try
        {
            var id = SelectedTemplate?.Id ?? 0; // 0 = insert, >0 = update (upsert in db)
            var t = new ArsenalTemplate(id, EditName.Trim(), EditCategory.Trim(), EditCommand.Trim(), EditDescription.Trim());
            var newId = await _arsenal.SaveAsync(t);
            await LoadAsync(seed: false);
            App.Current.Dispatcher.Invoke(() =>
            {
                var match = Templates.FirstOrDefault(x => x.Id == newId);
                if (match is not null) SelectedTemplate = match;
            });
            Status = $"enregistré '{t.Name}' (#{newId})";
        }
        catch (OperationCanceledException) { Status = "Annulé"; }
        catch (Exception ex) { _log.Error("arsenal", "Échec de l'enregistrement", ex); Status = "Erreur : " + ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task DeleteTemplateAsync(CancellationToken ct)
    {
        if (IsBusy) return;
        if (SelectedTemplate is not { } sel) { Status = "sélectionnez un modèle enregistré à supprimer"; return; }
        if (sel.Id <= 0) { Status = "brouillon non enregistré - rien à supprimer"; return; }
        IsBusy = true;
        try
        {
            await _arsenal.DeleteAsync(sel.Id);
            _log.Info("arsenal", $"modèle supprimé #{sel.Id}");
            SelectedTemplate = null;
            await LoadAsync(seed: false);
            Status = "supprimé";
        }
        catch (Exception ex) { _log.Error("arsenal", "Échec de la suppression", ex); Status = "Erreur : " + ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task SeedAsync(CancellationToken ct)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            await _arsenal.SeedIfEmptyAsync();
            await LoadAsync(seed: false);
            Status = "initialisation vérifiée (ajoute les 6 modèles intégrés lorsque la liste est vide)";
        }
        catch (Exception ex) { _log.Error("arsenal", "Échec de l'initialisation", ex); Status = "Erreur : " + ex.Message; }
        finally { IsBusy = false; }
    }

    // ---------------- execution ----------------

    [RelayCommand]
    private async Task ExecuteAsync(CancellationToken ct)
    {
        if (IsBusy) return;
        if (SelectedTemplate is not { } sel) { Status = "sélectionnez un modèle à exécuter"; return; }
        if (string.IsNullOrWhiteSpace(ExeIp)) { Status = "saisissez d'abord une IP cible"; return; }

        IsBusy = true;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Status = "exécution...";
        App.Current.Dispatcher.Invoke(() => Output.Clear());
        try
        {
            var prog = new Progress<string>(AddOutput);
            var text = await Task.Run(async () =>
                await _arsenal.ExecuteAsync(sel, ExeIp, ExePort, ExeMac, ExeGw, ExeMask, prog, _cts.Token),
                _cts.Token);
            Status = $"terminé ({text.Length} caractères de sortie)";
            _log.Info("arsenal", $"exécuté '{sel.Name}'");
        }
        catch (OperationCanceledException) { Status = "Annulé"; }
        catch (Exception ex) { _log.Error("arsenal", "Échec de l'exécution", ex); Status = "Erreur : " + ex.Message; }
        finally { IsBusy = false; _cts?.Dispose(); _cts = null; }
    }

    [RelayCommand]
    private void CancelExecute()
    {
        if (_cts is null) return;
        _cts.Cancel();
        Status = "annulation...";
    }
}
