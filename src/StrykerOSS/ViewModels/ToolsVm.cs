using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace StrykerOSS.ViewModels;

/// <summary>
/// VM du gestionnaire d'outils : détection réelle par outil (chemin + version) et
/// installations réelles (winget pour nmap/hashcat, téléchargeur auto pour nuclei,
/// MSI officiel pour Metasploit). Permet aussi de relancer l'assistant de première
/// configuration à tout moment.
/// </summary>
public partial class ToolsVm : ObservableObject
{
    private readonly IToolsManager _tools;
    private readonly ILogger _log;

    public ObservableCollection<ToolStatus> Tools { get; } = new();

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Prêt";
    [ObservableProperty] private string _toolsDir = "";

    public ToolsVm(IToolsManager tools, ILogger log)
    {
        _tools = tools;
        _log = log;
    }

    private bool CanInstall() => !IsBusy;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        IsBusy = true;
        Status = "Sonde de nmap / nuclei / msfvenom / hashcat / tshark en cours...";
        try
        {
            var list = await Task.Run(() => _tools.StatusAsync());
            await App.Current.Dispatcher.InvokeAsync(() =>
            {
                Tools.Clear();
                foreach (var t in list) Tools.Add(t);
            });
            ToolsDir = _tools.ToolsDir();
            Status = $"{list.Count(t => t.Found)} outil(s) installé(s) sur {list.Count}";
            _log.Info("tools-ui", $"actualisation : {Status}");
        }
        catch (Exception ex)
        {
            _log.Error("tools-ui", "vérification de l'état échouée", ex);
            Status = "Erreur : " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync(string? key)
    {
        if (IsBusy || string.IsNullOrWhiteSpace(key)) return;
        IsBusy = true;
        try
        {
            var progress = new Progress<string>(l =>
            {
                // garder la légende lisible : dernière ligne de progression, tronquée
                Status = l.Length > 140 ? l.Substring(0, 140) + "…" : l;
            });
            var ok = await Task.Run(() => _tools.InstallAsync(key, progress));
            Status = ok
                ? $"« {key} » installé avec succès — détection mise à jour ci-dessous."
                : $"L'installation de « {key} » n'a pas abouti — voir le dernier message ci-dessus.";
            _log.Info("tools-ui", $"installation « {key} » -> {ok}");
        }
        catch (Exception ex)
        {
            _log.Error("tools-ui", $"installation « {key} » échouée", ex);
            Status = "Erreur : " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
        await LoadAsync();
    }

    [RelayCommand]
    private void OpenSetupWizard()
    {
        try
        {
            var wizard = new Views.SetupWindow(App.Services);
            wizard.Owner = App.Current.MainWindow;
            wizard.ShowDialog();
            _ = LoadAsync(); // re-détection après le passage de l'assistant
        }
        catch (Exception ex)
        {
            _log.Error("tools-ui", "ouverture de l'assistant d'installation échouée", ex);
            Status = "Erreur : " + ex.Message;
        }
    }

    [RelayCommand]
    private void OpenToolsDir()
    {
        try
        {
            var dir = _tools.ToolsDir();
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{dir}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _log.Error("tools-ui", "ouverture du dossier des outils échouée", ex);
            Status = "Erreur : " + ex.Message;
        }
    }
}
