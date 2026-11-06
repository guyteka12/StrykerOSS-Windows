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
    [ObservableProperty] private string _status = "Ready";
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
            ? "Bluetooth radio detected - start a scan to discover real BLE devices."
            : "No Bluetooth radio On - enable Bluetooth in Windows settings.";
        _ble.Found += OnFound;
    }

    [RelayCommand]
    private async Task StartScanAsync(CancellationToken ct)
    {
        if (Scanning || IsBusy) return;
        if (!_ble.IsSupported)
        {
            Status = "Bluetooth radio is off or unavailable - cannot scan.";
            _log.Warn("ble", "scan requested with no radio");
            return;
        }
        IsBusy = true;
        try
        {
            Status = "Starting BLE active scan...";
            await _ble.StartAsync(new Progress<string>(l =>
                App.Current.Dispatcher.Invoke(() => Status = l)));
            Scanning = true;
            Status = "Scanning... (Fast Pair beacons are decoded)";
            _log.Info("ble", "scan started from UI");
        }
        catch (OperationCanceledException) { Status = "Cancelled"; }
        catch (Exception ex) { _log.Error("ble", "scan start failed", ex); Status = "Error: " + ex.Message; }
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
            Status = $"Scan stopped - {Devices.Count} device(s) seen.";
        }
        catch (Exception ex) { _log.Error("ble", "scan stop failed", ex); Status = "Error: " + ex.Message; }
    }

    [RelayCommand]
    private async Task EnumerateGattAsync(CancellationToken ct)
    {
        if (IsBusy || SelectedDevice is null) return;
        IsBusy = true;
        try
        {
            var address = SelectedDevice.Address;
            Status = $"Enumerating GATT on {address}...";
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
            Status = "GATT enumeration finished.";
        }
        catch (OperationCanceledException) { Status = "Cancelled"; }
        catch (Exception ex) { _log.Error("ble", "GATT enumeration failed", ex); Status = "Error: " + ex.Message; }
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
            if (Scanning) Status = $"Scanning... {Devices.Count} device(s) seen";
        });
    }
}

/// <summary>Row for the device list; provides display fallbacks and badge flags for the template.</summary>
public sealed record BleRow(BleDevice Device)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Device.Name) ? "(unnamed)" : Device.Name!;
    public string Address => Device.Address;
    public short Rssi => Device.Rssi;
    public bool IsFastPair => Device.IsFastPair;
    public string? ModelId => Device.ModelId;
    public string? Company => Device.Company;
    public bool HasCompany => !string.IsNullOrWhiteSpace(Device.Company);
    public string LastSeen => Device.LastSeen.ToString("HH:mm:ss");
}
