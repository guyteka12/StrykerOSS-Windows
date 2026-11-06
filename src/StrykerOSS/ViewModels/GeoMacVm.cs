using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using StrykerOSS.Core.Services;

namespace StrykerOSS.ViewModels;

public partial class GeoMacVm : ObservableObject
{
    private readonly IGeoMacService _geo;
    private readonly IDbService _db;
    private readonly ISettings _settings;
    private readonly ILogger _log;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Ready";
    [ObservableProperty] private string? _myIp;
    [ObservableProperty] private string _lookupIp = "";
    [ObservableProperty] private bool _hasLookup;
    [ObservableProperty] private string? _resQuery;
    [ObservableProperty] private string? _resCountry;
    [ObservableProperty] private string? _resRegion;
    [ObservableProperty] private string? _resCity;
    [ObservableProperty] private string? _resIsp;
    [ObservableProperty] private string? _resOrg;
    [ObservableProperty] private string? _resAs;
    [ObservableProperty] private string? _resCoords;
    [ObservableProperty] private GeoPoint? _selectedPoint;
    [ObservableProperty] private int _pointCount;

    public ObservableCollection<GeoPoint> Points { get; } = new();

    public GeoMacVm(IGeoMacService geo, IDbService db, ISettings settings, ILogger log)
    {
        _geo = geo;
        _db = db;
        _settings = settings;
        _log = log;
        _ = Task.Run(async () =>
        {
            try { await LoadPointsAsync(); }
            catch (Exception ex) { _log.Warn("geomac", $"initial load failed: {ex.Message}"); }
        });
    }

    // ---- public IP / lookup ----

    [RelayCommand]
    private async Task MyIpAsync(CancellationToken ct)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            Status = "Resolving public IP via ip-api.com...";
            var ip = await _geo.PublicIpAsync();
            MyIp = ip;
            if (string.IsNullOrEmpty(ip))
            {
                Status = "Could not resolve public IP.";
                return;
            }
            Status = $"Public IP {ip} - looking up geolocation...";
            await ApplyLookupAsync(ip, ct);
        }
        catch (OperationCanceledException) { Status = "Cancelled"; }
        catch (Exception ex) { _log.Error("geomac", "public IP lookup failed", ex); Status = "Error: " + ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task LookupAsync(CancellationToken ct)
    {
        if (IsBusy) return;
        if (string.IsNullOrWhiteSpace(LookupIp)) { Status = "Enter an IP to look up."; return; }
        IsBusy = true;
        try
        {
            Status = $"Looking up {LookupIp.Trim()}...";
            await ApplyLookupAsync(LookupIp.Trim(), ct);
        }
        catch (OperationCanceledException) { Status = "Cancelled"; }
        catch (Exception ex) { _log.Error("geomac", "lookup failed", ex); Status = "Error: " + ex.Message; }
        finally { IsBusy = false; }
    }

    private async Task ApplyLookupAsync(string ip, CancellationToken ct)
    {
        var info = await _geo.LookupIpAsync(ip, ct);
        App.Current.Dispatcher.Invoke(() =>
        {
            if (info is null)
            {
                HasLookup = false;
                Status = $"Lookup failed for {ip}.";
                return;
            }
            ResQuery = info.Query;
            ResCountry = info.Country;
            ResRegion = info.RegionName;
            ResCity = info.City;
            ResIsp = info.Isp;
            ResOrg = info.Org;
            ResAs = info.As;
            ResCoords = $"{info.Lat.ToString(CultureInfo.InvariantCulture)}, {info.Lon.ToString(CultureInfo.InvariantCulture)}";
            HasLookup = true;
            Status = $"Lookup OK: {info.City}, {info.Country}.";
        });
        _log.Info("geomac", $"IP lookup {ip}");
    }

    // ---- points ----

    [RelayCommand]
    private async Task ReloadAsync(CancellationToken ct)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            await LoadPointsAsync();
            Status = $"{Points.Count} geo point(s) loaded.";
        }
        catch (Exception ex) { _log.Error("geomac", "reload failed", ex); Status = "Error: " + ex.Message; }
        finally { IsBusy = false; }
    }

    private async Task LoadPointsAsync()
    {
        var list = await _db.GetGeoPointsAsync();
        App.Current.Dispatcher.Invoke(() =>
        {
            Points.Clear();
            foreach (var p in list.OrderByDescending(x => x.Added)) Points.Add(p);
            PointCount = Points.Count;
        });
    }

    [RelayCommand]
    private async Task ImportWiGleAsync(CancellationToken ct)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var dlg = new OpenFileDialog
            {
                Title = "Import WiGLE CSV",
                Filter = "WiGLE CSV (*.csv)|*.csv|All files (*.*)|*.*"
            };
            if (dlg.ShowDialog() != true)
            {
                Status = "Import cancelled.";
                return;
            }
            Status = "Parsing WiGLE CSV...";
            var pts = await _geo.ImportWiGleCsvAsync(dlg.FileName,
                new Progress<string>(l => App.Current.Dispatcher.Invoke(() => Status = l)));
            Status = $"Saving {pts.Count} point(s) to the database...";
            foreach (var p in pts) await _db.AddGeoPointAsync(p);
            await LoadPointsAsync();
            Status = $"Imported {pts.Count} WiGLE point(s).";
        }
        catch (OperationCanceledException) { Status = "Cancelled"; }
        catch (Exception ex) { _log.Error("geomac", "WiGLE import failed", ex); Status = "Error: " + ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ExportKmlAsync(CancellationToken ct)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            if (Points.Count == 0) { Status = "No geo points to export."; return; }
            var dlg = new SaveFileDialog
            {
                Title = "Export KML",
                Filter = "KML (*.kml)|*.kml",
                FileName = $"geomac_{DateTime.Now:yyyyMMdd_HHmmss}.kml",
                InitialDirectory = SafeExportsDir()
            };
            if (dlg.ShowDialog() != true) { Status = "Export cancelled."; return; }
            var path = await _geo.ExportKmlAsync(Points.ToList(), dlg.FileName);
            Status = $"KML exported: {path}";
        }
        catch (OperationCanceledException) { Status = "Cancelled"; }
        catch (Exception ex) { _log.Error("geomac", "KML export failed", ex); Status = "Error: " + ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ExportCsvAsync(CancellationToken ct)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            if (Points.Count == 0) { Status = "No geo points to export."; return; }
            var dlg = new SaveFileDialog
            {
                Title = "Export CSV",
                Filter = "CSV (*.csv)|*.csv",
                FileName = $"geomac_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
                InitialDirectory = SafeExportsDir()
            };
            if (dlg.ShowDialog() != true) { Status = "Export cancelled."; return; }
            var path = await _geo.ExportCsvAsync(Points.ToList(), dlg.FileName);
            Status = $"CSV exported: {path}";
        }
        catch (OperationCanceledException) { Status = "Cancelled"; }
        catch (Exception ex) { _log.Error("geomac", "CSV export failed", ex); Status = "Error: " + ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task DeletePointAsync(CancellationToken ct)
    {
        if (IsBusy || SelectedPoint is null) return;
        IsBusy = true;
        try
        {
            var label = SelectedPoint.Label;
            await _db.DeleteGeoPointAsync(SelectedPoint.Id);
            await LoadPointsAsync();
            Status = $"Deleted point '{label}'.";
        }
        catch (OperationCanceledException) { Status = "Cancelled"; }
        catch (Exception ex) { _log.Error("geomac", "delete failed", ex); Status = "Error: " + ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void OpenMap(GeoPoint? p)
    {
        var pt = p ?? SelectedPoint;
        if (pt is null) { Status = "Select a point first."; return; }
        if (pt.Lat is null || pt.Lon is null) { Status = "Point has no coordinates."; return; }
        var lat = pt.Lat.Value.ToString(CultureInfo.InvariantCulture);
        var lon = pt.Lon.Value.ToString(CultureInfo.InvariantCulture);
        var url = $"https://www.openstreetmap.org/?mlat={lat}&mlon={lon}#map=17/{lat}/{lon}";
        Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        Status = $"Opened OpenStreetMap for '{pt.Label}'.";
    }

    private string SafeExportsDir()
    {
        try { return Path.Combine(_settings.EnsureDataDir(), "exports"); }
        catch { return Environment.CurrentDirectory; }
    }
}
