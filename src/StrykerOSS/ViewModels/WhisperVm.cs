using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StrykerOSS.Core.Services;

namespace StrykerOSS.ViewModels;

public partial class WhisperVm : ObservableObject
{
    private readonly IBleService _ble;
    private readonly ILogger _log;
    private readonly Dictionary<string, int> _rowIndex = new();

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Prêt";
    [ObservableProperty] private bool _scanning;
    [ObservableProperty] private BleRow? _selectedDevice;
    [ObservableProperty] private int _deviceCount;

    public bool IsSupported { get; }
    public bool IsUnsupported { get; }

    public ObservableCollection<BleRow> Devices { get; } = new();
    public ObservableCollection<string> GattLines { get; } = new();

    public WhisperVm(IBleService ble, ILogger log)
    {
        _ble = ble;
        _log = log;
        IsSupported = ble.IsSupported;
        IsUnsupported = !ble.IsSupported;
        Status = ble.IsSupported
            ? "Radio Bluetooth détectée - démarrez un scan pour découvrir de vrais appareils BLE."
            : "Aucune radio Bluetooth activée - activez le Bluetooth dans les paramètres Windows.";
        _ble.Found += OnFound;
    }

    [RelayCommand]
    private async Task StartScanAsync(CancellationToken ct)
    {
        if (Scanning || IsBusy) return;
        if (!_ble.IsSupported)
        {
            Status = "La radio Bluetooth est désactivée ou indisponible - scan impossible.";
            _log.Warn("ble", "scan demandé sans radio");
            return;
        }
        IsBusy = true;
        try
        {
            Status = "Démarrage du scan BLE actif...";
            await _ble.StartAsync(new Progress<string>(l =>
                App.Current.Dispatcher.Invoke(() => Status = l)));
            Scanning = true;
            Status = "Scan en cours... (les balises Fast Pair sont décodées)";
            _log.Info("ble", "scan démarré depuis l'UI");
        }
        catch (OperationCanceledException) { Status = "Annulé"; }
        catch (Exception ex) { _log.Error("ble", "Échec du démarrage du scan", ex); Status = "Erreur : " + ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task StopScanAsync()
    {
        if (!Scanning) return;
        try
        {
            await _ble.StopAsync();
            Scanning = false;
            Status = $"Scan arrêté - {Devices.Count} appareil(s) vu(s).";
        }
        catch (Exception ex) { _log.Error("ble", "Échec de l'arrêt du scan", ex); Status = "Erreur : " + ex.Message; }
    }

    [RelayCommand]
    private async Task EnumerateGattAsync(CancellationToken ct)
    {
        if (IsBusy || SelectedDevice is null) return;
        IsBusy = true;
        try
        {
            var address = SelectedDevice.Address;
            Status = $"Énumération GATT sur {address}...";
            App.Current.Dispatcher.Invoke(() => GattLines.Clear());

            var report = await _ble.EnumerateGattAsync(address,
                new Progress<string>(l => App.Current.Dispatcher.Invoke(() =>
                {
                    if (GattLines.Count > 300) GattLines.RemoveAt(0);
                    GattLines.Add(l);
                })), ct);

            App.Current.Dispatcher.Invoke(() =>
            {
                foreach (var line in report.Replace("\r", "").Split('\n'))
                    if (line.Length > 0) GattLines.Add(line);
            });
            Status = "Énumération GATT terminée.";
        }
        catch (OperationCanceledException) { Status = "Annulé"; }
        catch (Exception ex) { _log.Error("ble", "Échec de l'énumération GATT", ex); Status = "Erreur : " + ex.Message; }
        finally { IsBusy = false; }
    }

    private void OnFound(BleDevice d)
    {
        App.Current.Dispatcher.Invoke(() =>
        {
            var row = new BleRow(d);
            if (_rowIndex.TryGetValue(d.Address, out var idx) && idx >= 0 && idx < Devices.Count)
            {
                Devices[idx] = row;
            }
            else
            {
                _rowIndex[d.Address] = Devices.Count;
                Devices.Add(row);
            }
            DeviceCount = Devices.Count;
            if (Scanning) Status = $"Scan en cours... {Devices.Count} appareil(s) vu(s)";
        });
    }
}

/// <summary>Row for the device list; provides display fallbacks and badge flags for the template.</summary>
public sealed record BleRow(BleDevice Device)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Device.Name) ? "(sans nom)" : Device.Name!;
    public string Address => Device.Address;
    public short Rssi => Device.Rssi;
    public bool IsFastPair => Device.IsFastPair;
    public string? ModelId => Device.ModelId;
    public string? Company => Device.Company;
    public bool HasCompany => !string.IsNullOrWhiteSpace(Device.Company);
    public string LastSeen => Device.LastSeen.ToString("HH:mm:ss");
}
