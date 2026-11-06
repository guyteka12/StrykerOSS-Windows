using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StrykerOSS.Core.Services;

namespace StrykerOSS.ViewModels;

/// <summary>Bindable wrapper around UsbDeviceInfo with precomputed display text.</summary>
public sealed class UsbDeviceRow
{
    public UsbDeviceRow(UsbDeviceInfo d) => Device = d;

    public UsbDeviceInfo Device { get; }

    public string PnpId => Device.PnpId;
    public string Name => string.IsNullOrWhiteSpace(Device.Name) ? "(unknown device)" : Device.Name!;
    public string? Manufacturer => Device.Manufacturer;
    public string Class => Device.Class;
    public bool IsStorage => Device.IsStorage;
    public string? DriveLetter => Device.DriveLetter;

    public string SizeText
    {
        get
        {
            if (Device.SizeBytes is not > 0) return string.Empty;
            var b = Device.SizeBytes!.Value;
            return b >= 1L << 30 ? $"{b / (double)(1L << 30):0.0#} GB" : $"{b / (double)(1L << 20):0.#} MB";
        }
    }

    public string DetailText
    {
        get
        {
            if (!IsStorage) return Class;
            var size = string.IsNullOrEmpty(SizeText) ? "" : $" · {SizeText}";
            return $"Removable volume · {Device.DriveLetter}{size}";
        }
    }
}

/// <summary>
/// USB Arsenal VM: real PnP USB enumeration, real ISO mounting (Mount-DiskImage),
/// real Shell-COM eject of removable drives.
/// </summary>
public partial class UsbVm : ObservableObject
{
    private readonly IUsbService _usb;
    private readonly ILogger _log;

    public ObservableCollection<UsbDeviceRow> Devices { get; } = new();
    public ObservableCollection<string> Log { get; } = new();

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Ready";
    [ObservableProperty] private string _isoPath = "";
    [ObservableProperty] private int _storageCount;

    public UsbVm(IUsbService usb, ILogger log)
    {
        _usb = usb;
        _log = log;
        AddLog("USB Arsenal ready - click Refresh to enumerate real devices.");
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        IsBusy = true;
        Status = "Enumerating USB devices (Get-PnpEntity + Win32_LogicalDisk) ...";
        try
        {
            var list = await Task.Run(() => _usb.ListUsbDevicesAsync());
            var rows = list.Select(d => new UsbDeviceRow(d)).ToList();
            await App.Current.Dispatcher.InvokeAsync(() =>
            {
                Devices.Clear();
                foreach (var r in rows) Devices.Add(r);
            });
            StorageCount = list.Count(d => d.IsStorage);
            Status = $"{list.Count} entries ({StorageCount} removable volumes)";
            _log.Info("usb-ui", $"refreshed: {Status}");
        }
        catch (Exception ex)
        {
            _log.Error("usb-ui", "USB enumeration failed", ex);
            Status = "Error: " + ex.Message;
            AddLog($"[error] {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void BrowseIso()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select ISO image",
            Filter = "ISO images (*.iso)|*.iso|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dlg.ShowDialog() == true)
        {
            IsoPath = dlg.FileName;
            AddLog($"Selected ISO: {IsoPath}");
        }
    }

    [RelayCommand]
    private async Task MountAsync()
    {
        if (IsBusy || string.IsNullOrWhiteSpace(IsoPath)) return;
        IsBusy = true;
        var ok = false;
        try
        {
            var progress = new Progress<string>(AddLog);
            ok = await Task.Run(() => _usb.MountIsoAsync(IsoPath, progress));
            Status = ok ? "ISO mounted - new volume available." : "ISO mount failed - see log.";
            _log.Info("usb-ui", $"mount '{IsoPath}' -> {ok}");
        }
        catch (Exception ex)
        {
            _log.Error("usb-ui", "ISO mount failed", ex);
            Status = "Error: " + ex.Message;
            AddLog($"[error] {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
        if (ok) await LoadAsync(); // show the new volume in the list
    }

    [RelayCommand]
    private async Task EjectAsync(string? pnpId)
    {
        if (IsBusy || string.IsNullOrWhiteSpace(pnpId) || !pnpId.StartsWith("VOLUME:", StringComparison.Ordinal)) return;
        var letter = pnpId["VOLUME:".Length..].Trim('\\', ':', ' ');
        IsBusy = true;
        try
        {
            var progress = new Progress<string>(AddLog);
            var ok = await Task.Run(() => _usb.EjectDriveAsync(letter, progress));
            Status = ok ? $"Eject requested for {letter}: - verify in Explorer." : "Eject failed - see log.";
            _log.Info("usb-ui", $"eject {letter}: -> {ok}");
        }
        catch (Exception ex)
        {
            _log.Error("usb-ui", $"eject {letter} failed", ex);
            Status = "Error: " + ex.Message;
            AddLog($"[error] {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
        await LoadAsync();
    }

    private void AddLog(string line)
    {
        void Add()
        {
            Log.Add(line);
            while (Log.Count > 2000) Log.RemoveAt(0);
        }
        var d = App.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) Add();
        else d.Invoke(Add);
    }
}
