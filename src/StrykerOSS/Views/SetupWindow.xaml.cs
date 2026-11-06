using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using StrykerOSS.Core;
using StrykerOSS.Core.Services;

namespace StrykerOSS.Views;

/// <summary>
/// Assistant de première configuration : détecte puis installe RÉELLEMENT chaque
/// outil (winget / MSI / archives officielles). Toutes les étapes sont journalisées
/// en direct et l'utilisateur reste maître de la sélection.
/// </summary>
public partial class SetupWindow : Window
{
    public sealed class Row : ObservableObject
    {
        public string Key { get; init; } = "";
        public string Name { get; init; } = "";
        public string Description { get; init; } = "";
        public string SizeHint { get; init; } = "";
        public bool Optional { get; init; }

        private bool _installed;
        public bool Installed { get => _installed; set { SetProperty(ref _installed, value); OnPropertyChanged(nameof(CanInstall)); } }

        private bool _selected = true;
        public bool Selected { get => _selected; set => SetProperty(ref _selected, value); }

        public bool CanInstall => !Installed;

        private string _state = "Idle"; // Idle | Installing | Done | Failed
        public string State { get => _state; set { SetProperty(ref _state, value); OnPropertyChanged(nameof(StatusText)); } }

        public string StatusText => State switch
        {
            "Installing" => "INSTALLATION...",
            "Done" => "INSTALLÉ",
            "Failed" => "ÉCHEC",
            _ => Installed ? "DÉJÀ PRÉSENT" : "MANQUANT",
        };
    }

    private readonly IProvisioning _provisioning;
    private readonly ILogger _log;
    private readonly ObservableCollection<Row> _rows = new();
    private bool _busy;

    public SetupWindow(IServiceProvider sp)
    {
        InitializeComponent();
        _provisioning = sp.GetRequiredService<IProvisioning>();
        _log = sp.GetRequiredService<ILogger>();
        ItemsList.ItemsSource = _rows;
        Loaded += async (_, _) => await RefreshAsync();
    }

    private void AddLog(string line)
    {
        LogList.Items.Add($"[{DateTime.Now:HH:mm:ss}] {line}");
        while (LogList.Items.Count > 600) LogList.Items.RemoveAt(0);
        if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[^1]);
    }

    private async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var items = await _provisioning.CheckAsync();
            _rows.Clear();
            foreach (var i in items)
            {
                _rows.Add(new Row
                {
                    Key = i.Key,
                    Name = i.Name,
                    Description = i.Description,
                    SizeHint = i.SizeHint,
                    Optional = i.Optional,
                    Installed = i.Installed,
                    Selected = !i.Installed,
                });
            }
            AddLog("Détection terminée : " +
                   $"{_rows.Count(r => r.Installed)} composant(s) déjà présent(s) sur {_rows.Count}.");
        }
        catch (Exception ex)
        {
            _log.Error("setup", "détection des composants échouée", ex);
            AddLog("Erreur de détection : " + ex.Message);
        }
        finally { _busy = false; }
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void OnInstallAll(object sender, RoutedEventArgs e)
    {
        foreach (var r in _rows) r.Selected = !r.Installed;
        await InstallSelectedAsync();
    }

    private async void OnInstallSelected(object sender, RoutedEventArgs e) => await InstallSelectedAsync();

    private async Task InstallSelectedAsync()
    {
        if (_busy) return;
        var targets = _rows.Where(r => r.Selected && !r.Installed).ToList();
        if (targets.Count == 0)
        {
            AddLog("Aucun composant à installer (déjà tout installé, ou rien de sélectionné).");
            return;
        }

        _busy = true;
        BtnInstallAll.IsEnabled = false;
        BtnInstallSel.IsEnabled = false;
        BtnRefresh.IsEnabled = false;
        BtnClose.Content = "Installation en cours...";
        Progress.Visibility = Visibility.Visible;

        var progress = new Progress<string>(s => Dispatcher.Invoke(() => AddLog(s)));

        try
        {
            foreach (var row in targets)
            {
                row.State = "Installing";
                AddLog($"―――― {row.Name} ――――");
                bool ok;
                try { ok = await _provisioning.InstallAsync(row.Key, progress); }
                catch (Exception ex)
                {
                    _log.Error("setup", $"installation de {row.Key} échouée", ex);
                    AddLog($"Erreur : {ex.Message}");
                    ok = false;
                }
                row.Installed = ok; // la re-détection finale corrige au besoin
                row.State = ok ? "Done" : "Failed";
                if (ok) row.Selected = false;
            }

            // re-détection réelle après installation
            AddLog("Re-détection finale des composants...");
            var items = await _provisioning.CheckAsync();
            foreach (var it in items)
            {
                var row = _rows.FirstOrDefault(r => r.Key == it.Key);
                if (row != null)
                {
                    row.Installed = it.Installed;
                    if (it.Installed && row.State != "Failed") row.State = "Done";
                    if (!it.Installed && row.State == "Done") row.State = "Idle";
                }
            }

            int okCount = _rows.Count(r => r.Installed);
            AddLog($"Terminé : {okCount}/{_rows.Count} composant(s) opérationnel(s). " +
                   "Vous pouvez fermer l'assistant ; les outils restants restent installables via l'onglet Outils.");
        }
        finally
        {
            _busy = false;
            Progress.Visibility = Visibility.Collapsed;
            BtnInstallAll.IsEnabled = true;
            BtnInstallSel.IsEnabled = true;
            BtnRefresh.IsEnabled = true;
            BtnClose.Content = "Fermer l'assistant";
        }
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            var r = MessageBox.Show("Une installation est en cours. Fermer quand même ?",
                "StrykerOSS", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;
        }
        Close();
    }
}
