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
    [ObservableProperty] private string _status = "Ready";
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
        Status = "Loading adapters and saved profiles...";
        try
        {
            await RefreshAdaptersAsync();
            var profiles = await _wifi.GetProfilesAsync();
            App.Current.Dispatcher.Invoke(() =>
            {
                Profiles.Clear();
                foreach (var p in profiles) Profiles.Add(new WifiProfileRow(p.Name, p.Auth));
            });
            Status = $"Ready - {Adapters.Count} adapter(s), {Profiles.Count} profile(s)";
            _log.Info("wifi", $"loaded {Adapters.Count} adapter(s), {Profiles.Count} profile(s)");
        }
        catch (Exception ex)
        {
            _log.Error("wifi", "loading adapters/profiles failed", ex);
            Status = "Error: " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task ScanAsync(CancellationToken ct)
    {
        if (IsBusy) return;
        IsBusy = true;
        Status = "Scanning...";
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
            Status = $"Found {networks.Count} network(s)";
            _log.Info("wifi", $"scan finished: {networks.Count} network(s)");
        }
        catch (OperationCanceledException)
        {
            Status = "Scan cancelled";
            _log.Warn("wifi", "scan cancelled");
        }
        catch (Exception ex)
        {
            _log.Error("wifi", "scan failed", ex);
            Status = "Error: " + ex.Message;
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
            Status = "Hidden network - its SSID is unknown, cannot build a connection profile.";
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
                Status = wep ? "WEP key must be 5/13 characters or 10/26 hex digits." : "Passphrase must be 8-63 characters.";
                return;
            }
        }

        IsBusy = true;
        Status = $"Connecting to '{net.ConnectSsid}'...";
        try
        {
            var req = new WifiConnectRequest(net.ConnectSsid, Password, net.ConnectAuth);
            bool ok = await _wifi.ConnectAsync(req);
            await RefreshAdaptersAsync();
            await UpdateConnectedFlagsAsync();
            Status = ok
                ? $"Connected to '{net.ConnectSsid}'."
                : $"Could not confirm connection to '{net.ConnectSsid}' (15 s timeout).";
            _log.Info("wifi", $"connect '{net.ConnectSsid}': {(ok ? "connected" : "timeout")}");
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled";
        }
        catch (Exception ex)
        {
            _log.Error("wifi", $"connect to '{net.ConnectSsid}' failed", ex);
            Status = "Error: " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    private bool CanConnect() => !IsBusy && SelectedNetwork != null;

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        Status = "Disconnecting...";
        try
        {
            await _wifi.DisconnectAsync();
            await RefreshAdaptersAsync();
            await UpdateConnectedFlagsAsync();
            Status = "Disconnected.";
            _log.Info("wifi", "disconnect issued");
        }
        catch (Exception ex)
        {
            _log.Error("wifi", "disconnect failed", ex);
            Status = "Error: " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task DeleteProfileAsync(WifiProfileRow? row)
    {
        if (IsBusy || row is null) return;
        IsBusy = true;
        Status = $"Deleting profile '{row.Name}'...";
        try
        {
            bool ok = await _wifi.DeleteProfileAsync(row.Name);
            App.Current.Dispatcher.Invoke(() => Profiles.Remove(row));
            Status = ok ? $"Profile '{row.Name}' deleted." : $"Profile '{row.Name}' was not found.";
            _log.Info("wifi", $"delete profile '{row.Name}': {ok}");
        }
        catch (Exception ex)
        {
            _log.Error("wifi", $"delete profile '{row.Name}' failed", ex);
            Status = "Error: " + ex.Message;
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
        string ssid = string.IsNullOrEmpty(n.Ssid) ? "<hidden>" : n.Ssid;
        string bssid = string.IsNullOrWhiteSpace(n.Bssid) ? "BSSID unknown" : "BSSID " + n.Bssid;
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
