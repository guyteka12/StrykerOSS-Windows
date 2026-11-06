using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace StrykerOSS.ViewModels;

// Display rows kept as records so XAML templates bind to plain properties.
public sealed record WifiAdapterRow(string Name, string Description, string State, bool Connected, string SsidText);

public sealed record WifiNetworkRow(string Ssid, string Bssid, string Channel, string Band,
    string Signal, string Auth, string Encryption, bool Connected, string ConnectSsid, string ConnectAuth);

public sealed record WifiProfileRow(string Name, string Auth);

public partial class WifiVm : ObservableObject
{
    private readonly IWifiService _wifi;
    private readonly ILogger _log;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Prêt";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private WifiNetworkRow? _selectedNetwork;
    [ObservableProperty] private WifiProfileRow? _selectedProfile;

    public ObservableCollection<WifiAdapterRow> Adapters { get; } = new();
    public ObservableCollection<WifiNetworkRow> Networks { get; } = new();
    public ObservableCollection<WifiProfileRow> Profiles { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();

    public WifiVm(IWifiService wifi, ILogger log)
    {
        _wifi = wifi;
        _log = log;
        _ = LoadAsync();
    }

    partial void OnSelectedNetworkChanged(WifiNetworkRow? value)
    {
        Password = "";
        ConnectCommand.NotifyCanExecuteChanged();
    }

    // ---- commands ----

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        Status = "Chargement des adaptateurs et des profils enregistrés...";
        try
        {
            await RefreshAdaptersAsync();
            var profiles = await _wifi.GetProfilesAsync();
            App.Current.Dispatcher.Invoke(() =>
            {
                Profiles.Clear();
                foreach (var p in profiles) Profiles.Add(new WifiProfileRow(p.Name, p.Auth));
            });
            Status = $"Prêt — {Adapters.Count} adaptateur(s), {Profiles.Count} profil(s)";
            _log.Info("wifi", $"{Adapters.Count} adaptateur(s), {Profiles.Count} profil(s) chargés");
        }
        catch (Exception ex)
        {
            _log.Error("wifi", "échec du chargement des adaptateurs/profils", ex);
            Status = "Erreur : " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task ScanAsync(CancellationToken ct)
    {
        if (IsBusy) return;
        IsBusy = true;
        Status = "Analyse en cours...";
        try
        {
            var progress = new Progress<string>(s => App.Current.Dispatcher.Invoke(() => AddLog(s)));
            var networks = await _wifi.ScanAsync(progress, ct);
            await RefreshAdaptersAsync();
            App.Current.Dispatcher.Invoke(() =>
            {
                Networks.Clear();
                foreach (var n in networks) Networks.Add(ToRow(n));
                SelectedNetwork = null;
            });
            Status = $"{networks.Count} réseau(x) trouvé(s)";
            _log.Info("wifi", $"analyse terminée : {networks.Count} réseau(s)");
        }
        catch (OperationCanceledException)
        {
            Status = "Analyse annulée";
            _log.Warn("wifi", "analyse annulée");
        }
        catch (Exception ex)
        {
            _log.Error("wifi", "échec de l'analyse", ex);
            Status = "Erreur : " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        if (IsBusy || SelectedNetwork is null) return;
        var net = SelectedNetwork;

        if (string.IsNullOrWhiteSpace(net.ConnectSsid))
        {
            Status = "Réseau masqué — son SSID est inconnu, impossible de créer un profil de connexion.";
            return;
        }

        bool open = string.Equals(net.ConnectAuth, "Open", StringComparison.OrdinalIgnoreCase);
        bool wep = string.Equals(net.ConnectAuth, "WEP", StringComparison.OrdinalIgnoreCase);
        if (!open)
        {
            int len = Password.Length;
            bool lengthOk = wep ? len is 5 or 13 or 10 or 26 : len is >= 8 and <= 63;
            if (!lengthOk)
            {
                Status = wep ? "La clé WEP doit comporter 5/13 caractères ou 10/26 chiffres hexadécimaux." : "La phrase secrète doit contenir 8 à 63 caractères.";
                return;
            }
        }

        IsBusy = true;
        Status = $"Connexion à '{net.ConnectSsid}'...";
        try
        {
            var req = new WifiConnectRequest(net.ConnectSsid, Password, net.ConnectAuth);
            bool ok = await _wifi.ConnectAsync(req);
            await RefreshAdaptersAsync();
            await UpdateConnectedFlagsAsync();
            Status = ok
                ? $"Connecté à '{net.ConnectSsid}'."
                : $"Impossible de confirmer la connexion à '{net.ConnectSsid}' (délai de 15 s dépassé).";
            _log.Info("wifi", $"connexion '{net.ConnectSsid}' : {(ok ? "connecté" : "délai dépassé")}");
        }
        catch (OperationCanceledException)
        {
            Status = "Annulé";
        }
        catch (Exception ex)
        {
            _log.Error("wifi", $"échec de la connexion à '{net.ConnectSsid}'", ex);
            Status = "Erreur : " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    private bool CanConnect() => !IsBusy && SelectedNetwork != null;

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        Status = "Déconnexion...";
        try
        {
            await _wifi.DisconnectAsync();
            await RefreshAdaptersAsync();
            await UpdateConnectedFlagsAsync();
            Status = "Déconnecté.";
            _log.Info("wifi", "déconnexion demandée");
        }
        catch (Exception ex)
        {
            _log.Error("wifi", "échec de la déconnexion", ex);
            Status = "Erreur : " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task DeleteProfileAsync(WifiProfileRow? row)
    {
        if (IsBusy || row is null) return;
        IsBusy = true;
        Status = $"Suppression du profil '{row.Name}'...";
        try
        {
            bool ok = await _wifi.DeleteProfileAsync(row.Name);
            App.Current.Dispatcher.Invoke(() => Profiles.Remove(row));
            Status = ok ? $"Profil '{row.Name}' supprimé." : $"Profil '{row.Name}' introuvable.";
            _log.Info("wifi", $"suppression du profil '{row.Name}' : {ok}");
        }
        catch (Exception ex)
        {
            _log.Error("wifi", $"échec de la suppression du profil '{row.Name}'", ex);
            Status = "Erreur : " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    // ---- helpers ----

    private async Task RefreshAdaptersAsync()
    {
        var adapters = await _wifi.GetAdaptersAsync();
        App.Current.Dispatcher.Invoke(() =>
        {
            Adapters.Clear();
            foreach (var a in adapters)
            {
                string ssidText = string.IsNullOrEmpty(a.ConnectedSsid) ? "" : $"SSID: {a.ConnectedSsid}";
                Adapters.Add(new WifiAdapterRow(a.Name, a.Description, a.State.ToUpperInvariant(), a.Connected, ssidText));
            }
        });
    }

    private async Task UpdateConnectedFlagsAsync()
    {
        var conn = await _wifi.GetCurrentConnectionAsync();
        App.Current.Dispatcher.Invoke(() =>
        {
            for (int i = 0; i < Networks.Count; i++)
            {
                var r = Networks[i];
                bool nowConnected = conn != null && !string.IsNullOrEmpty(conn.ConnectedSsid) && r.ConnectSsid == conn.ConnectedSsid;
                if (r.Connected != nowConnected)
                    Networks[i] = r with { Connected = nowConnected };
            }
        });
    }

    private static WifiNetworkRow ToRow(WifiNetwork n)
    {
        string ssid = string.IsNullOrEmpty(n.Ssid) ? "<masqué>" : n.Ssid;
        string bssid = string.IsNullOrWhiteSpace(n.Bssid) ? "BSSID inconnu" : "BSSID " + n.Bssid;
        return new WifiNetworkRow(
            ssid,
            bssid,
            n.Channel > 0 ? $"CH {n.Channel}" : "CH ?",
            string.IsNullOrWhiteSpace(n.Band) ? "?" : n.Band,
            $"{n.SignalPercent}%",
            string.IsNullOrWhiteSpace(n.Auth) ? "?" : n.Auth,
            string.IsNullOrWhiteSpace(n.Encryption) ? "?" : n.Encryption,
            n.Connected,
            n.Ssid,
            n.Auth);
    }

    private void AddLog(string line)
    {
        LogLines.Add($"[{DateTime.Now:HH:mm:ss}] {line}");
        while (LogLines.Count > 400) LogLines.RemoveAt(0);
    }
}

/// <summary>
/// Enables two-way binding for PasswordBox.Password (which is not a dependency
/// property) without any code-behind logic in the view:
///   vms:PasswordHelper.Bind="True"
///   vms:PasswordHelper.BoundPassword="{Binding Password, Mode=TwoWay}"
/// </summary>
public static class PasswordHelper
{
    public static readonly DependencyProperty BoundPasswordProperty = DependencyProperty.RegisterAttached(
        "BoundPassword", typeof(string), typeof(PasswordHelper),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnBoundPasswordChanged));

    public static string GetBoundPassword(DependencyObject obj) => (string)obj.GetValue(BoundPasswordProperty);
    public static void SetBoundPassword(DependencyObject obj, string value) => obj.SetValue(BoundPasswordProperty, value);

    public static readonly DependencyProperty BindProperty = DependencyProperty.RegisterAttached(
        "Bind", typeof(bool), typeof(PasswordHelper), new PropertyMetadata(false, OnBindChanged));

    public static bool GetBind(DependencyObject obj) => (bool)obj.GetValue(BindProperty);
    public static void SetBind(DependencyObject obj, bool value) => obj.SetValue(BindProperty, value);

    private static void OnBindChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PasswordBox pb && (bool)e.NewValue)
        {
            pb.PasswordChanged += (_, _) =>
            {
                var current = (string?)pb.GetValue(BoundPasswordProperty) ?? "";
                if (current != pb.Password) pb.SetValue(BoundPasswordProperty, pb.Password);
            };
        }
    }

    private static void OnBoundPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is PasswordBox pb)
        {
            string pw = (string?)e.NewValue ?? "";
            if (pb.Password != pw) pb.Password = pw; // pushes VM -> box without re-entry loops
        }
    }
}
