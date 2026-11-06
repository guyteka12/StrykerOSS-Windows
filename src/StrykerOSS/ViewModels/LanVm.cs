using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StrykerOSS.Core.Services;

namespace StrykerOSS.ViewModels;

/// <summary>
/// Local Network module: real gateway/CIDR detection, ICMP discovery with
/// ARP + reverse DNS + OUI vendor, per-device common-ports scan and re-probe.
/// Every discovered run is recorded to the database as a "lan-discover" scan.
/// </summary>
public partial class LanVm : ObservableObject
{
    private readonly ILanService _lan;
    private readonly IPortScanner _ports;
    private readonly IOuiService _oui;
    private readonly IDbService _db;
    private readonly ILogger _log;

    public ObservableCollection<LanDeviceRow> Devices { get; } = new();

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _gateway = "";
    [ObservableProperty] private string _cidr = "";
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _status = "Prêt";
    [ObservableProperty] private string _ouiInfo = "Base OUI : chargement...";

    public LanVm(ILanService lan, IPortScanner ports, IOuiService oui, IDbService db, ILogger log)
    {
        _lan = lan;
        _ports = ports;
        _oui = oui;
        _db = db;
        _log = log;

        // auto-detect network on first show; InitAsync never throws
        _ = Task.Run(InitAsync);
    }

    private async Task InitAsync()
    {
        try
        {
            await _oui.InitializeAsync();
            Ui(() => OuiInfo = $"Base OUI : {_oui.EntryCount:N0} fabricants référencés");
            await DetectCoreAsync();
        }
        catch (Exception ex)
        {
            _log.Error("lan", "échec de l'initialisation", ex);
            Ui(() => Status = "Erreur d'initialisation : " + ex.Message);
        }
    }

    [RelayCommand]
    private Task DetectAsync()
    {
        if (IsBusy) return Task.CompletedTask;
        return DetectCoreAsync();
    }

    private async Task DetectCoreAsync()
    {
        try
        {
            var (gw, cidr) = await _lan.DetectNetworkAsync();
            Ui(() =>
            {
                Gateway = gw;
                Cidr = cidr;
                Status = cidr.Length == 0
                    ? "Aucun réseau Ethernet/Wi-Fi actif avec passerelle détecté"
                    : $"Réseau {cidr} via la passerelle {gw}";
            });
            _log.Info("lan", $"détection : passerelle={gw} cidr={cidr}");
        }
        catch (Exception ex)
        {
            _log.Error("lan", "échec de la détection réseau", ex);
            Ui(() => Status = "Échec de la détection : " + ex.Message);
        }
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task DiscoverAsync(CancellationToken ct)
    {
        if (IsBusy) return;
        IsBusy = true;
        Ui(() => { Devices.Clear(); ProgressText = ""; Status = "Découverte des hôtes en cours..."; });

        var sw = Stopwatch.StartNew();
        var started = DateTime.Now;
        try
        {
            var progress = new Progress<string>(s => ProgressText = s);
            var devices = await Task.Run(() => _lan.DiscoverAsync(progress, ct), ct);
            sw.Stop();

            Ui(() =>
            {
                foreach (var d in devices) Devices.Add(new LanDeviceRow(this, d));
                ProgressText = "";
                Status = $"{devices.Count} appareils découverts en {sw.Elapsed.TotalSeconds:0.0} s";
            });

            await _db.AddScanAsync(new ScanRecord(0, null, "lan-discover", Cidr, started,
                (int)sw.Elapsed.TotalMilliseconds, $"{devices.Count} appareils", null));
            _log.Info("lan", $"découverte terminée : {devices.Count} appareils sur {Cidr}");
        }
        catch (OperationCanceledException)
        {
            Ui(() => { ProgressText = ""; Status = "Annulé"; });
            _log.Warn("lan", "découverte annulée");
        }
        catch (Exception ex)
        {
            _log.Error("lan", "échec de la découverte", ex);
            Ui(() => Status = "Erreur : " + ex.Message);
        }
        finally
        {
            Ui(() => IsBusy = false);
        }
    }

    // ---------------- per-device operations (called by LanDeviceRow commands) ----------------

    internal async Task ScanPortsAsync(LanDeviceRow row)
    {
        if (row.IsScanning) return;
        row.IsScanning = true;
        try
        {
            var results = await Task.Run(() =>
                _ports.ScanTcpAsync(row.Ip, _ports.CommonPorts, 800, 200));
            var open = results.Where(r => r.Open).Select(r => r.Port).OrderBy(p => p).ToList();
            row.SetPorts(open);
            var banners = results.Where(r => r.Open && r.Banner != null)
                                 .Select(r => $"{r.Port}: {r.Banner}").ToList();
            _log.Info("lan", $"analyse de ports {row.Ip} : {open.Count} ouverts sur {_ports.CommonPorts.Count}" +
                            (banners.Count > 0 ? $" - {string.Join(" | ", banners)}" : ""));
        }
        catch (Exception ex)
        {
            _log.Error("lan", $"échec de l'analyse de ports pour {row.Ip}", ex);
            row.PortsSummary = "échec de l'analyse";
        }
        finally
        {
            row.IsScanning = false;
        }
    }

    internal async Task PingRowAsync(LanDeviceRow row)
    {
        if (row.IsScanning) return;
        row.IsScanning = true;
        try
        {
            var dev = await Task.Run(() => _lan.ProbeAsync(row.Ip));
            if (dev == null)
            {
                row.Alive = false;
                row.StatusNote = "hors ligne";
            }
            else
            {
                row.UpdateFrom(dev);
                row.Alive = true;
                row.StatusNote = $"{dev.LatencyMs} ms";
            }
        }
        catch (Exception ex)
        {
            _log.Error("lan", $"échec du ping pour {row.Ip}", ex);
            row.StatusNote = "erreur de ping";
        }
        finally
        {
            row.IsScanning = false;
        }
    }

    private static void Ui(Action action)
    {
        var app = Application.Current;
        if (app == null) return;
        if (app.Dispatcher.CheckAccess()) action();
        else app.Dispatcher.Invoke(action);
    }
}

/// <summary>Row wrapper for a discovered device - exposes mutable state + row commands.</summary>
public partial class LanDeviceRow : ObservableObject
{
    private readonly LanVm _parent;

    public LanDeviceRow(LanVm parent, LanDevice device)
    {
        _parent = parent;
        _device = device;
        _alive = true;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Hostname), nameof(Mac), nameof(Vendor),
        nameof(LatencyMs), nameof(IsGateway), nameof(LatencyText),
        nameof(HostText), nameof(HasHost), nameof(MacText), nameof(VendorText), nameof(HasVendor))]
    private LanDevice _device;

    [ObservableProperty] private bool _alive = true;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private string _portsSummary = "";
    [ObservableProperty] private string _openPortsText = "";
    [ObservableProperty] private string _statusNote = "";

    public string Ip => Device.Ip;
    public string? Hostname => Device.Hostname;
    public string? Mac => Device.Mac;
    public string? Vendor => Device.Vendor;
    public bool IsGateway => Device.IsGateway;
    public int LatencyMs => Device.LatencyMs;

    public string HostText => string.IsNullOrWhiteSpace(Hostname) ? "" : Hostname!;
    public bool HasHost => HostText.Length > 0;
    public string MacText => string.IsNullOrWhiteSpace(Mac) ? "MAC non résolue" : Mac!;
    public string VendorText => string.IsNullOrWhiteSpace(Vendor) ? "" : Vendor!;
    public bool HasVendor => VendorText.Length > 0;
    public string LatencyText => $"{LatencyMs} ms";

    public void UpdateFrom(LanDevice d) => Device = d;

    internal void SetPorts(List<int> ports)
    {
        Device = Device with { OpenPorts = ports };
        PortsSummary = $"{ports.Count} ouvert(s) (ports courants)";
        OpenPortsText = ports.Count == 0 ? "aucun port ouvert trouvé" : string.Join(", ", ports);
    }

    [RelayCommand]
    private Task PortsAsync() => _parent.ScanPortsAsync(this);

    [RelayCommand]
    private Task PingAsync() => _parent.PingRowAsync(this);
}
