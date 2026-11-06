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
    [ObservableProperty] private string _status = "Prêt";
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
            catch (Exception ex) { _log.Warn("geomac", $"échec du chargement initial : {ex.Message}"); }
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
            Status = "Résolution de l'IP publique via ip-api.com...";
            var ip = await _geo.PublicIpAsync();
            MyIp = ip;
            if (string.IsNullOrEmpty(ip))
            {
                Status = "Impossible de résoudre l'IP publique.";
                return;
            }
            Status = $"IP publique {ip} - localisation géographique en cours...";
            await ApplyLookupAsync(ip, ct);
        }
        catch (OperationCanceledException) { Status = "Annulé"; }
        catch (Exception ex) { _log.Error("geomac", "échec de la localisation de l'IP publique", ex); Status = "Erreur : " + ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task LookupAsync(CancellationToken ct)
    {
        if (IsBusy) return;
        if (string.IsNullOrWhiteSpace(LookupIp)) { Status = "Saisissez une IP à localiser."; return; }
        IsBusy = true;
        try
        {
            Status = $"Localisation de {LookupIp.Trim()}...";
            await ApplyLookupAsync(LookupIp.Trim(), ct);
        }
        catch (OperationCanceledException) { Status = "Annulé"; }
        catch (Exception ex) { _log.Error("geomac", "échec de la localisation", ex); Status = "Erreur : " + ex.Message; }
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
                Status = $"Échec de la localisation pour {ip}.";
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
            Status = $"Localisation réussie : {info.City}, {info.Country}.";
        });
        _log.Info("geomac", $"Localisation IP {ip}");
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
            Status = $"{Points.Count} point(s) géolocalisé(s) chargé(s).";
        }
        catch (Exception ex) { _log.Error("geomac", "échec du rechargement", ex); Status = "Erreur : " + ex.Message; }
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
                Title = "Importer un CSV WiGLE",
                Filter = "CSV WiGLE (*.csv)|*.csv|Tous les fichiers (*.*)|*.*"
            };
            if (dlg.ShowDialog() != true)
            {
                Status = "Import annulé.";
                return;
            }
            Status = "Analyse du CSV WiGLE...";
            var pts = await _geo.ImportWiGleCsvAsync(dlg.FileName,
                new Progress<string>(l => App.Current.Dispatcher.Invoke(() => Status = l)));
            Status = $"Enregistrement de {pts.Count} point(s) en base de données...";
            foreach (var p in pts) await _db.AddGeoPointAsync(p);
            await LoadPointsAsync();
            Status = $"{pts.Count} point(s) WiGLE importé(s).";
        }
        catch (OperationCanceledException) { Status = "Annulé"; }
        catch (Exception ex) { _log.Error("geomac", "échec de l'import WiGLE", ex); Status = "Erreur : " + ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ExportKmlAsync(CancellationToken ct)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            if (Points.Count == 0) { Status = "Aucun point géolocalisé à exporter."; return; }
            var dlg = new SaveFileDialog
            {
                Title = "Exporter en KML",
                Filter = "KML (*.kml)|*.kml",
                FileName = $"geomac_{DateTime.Now:yyyyMMdd_HHmmss}.kml",
                InitialDirectory = SafeExportsDir()
            };
            if (dlg.ShowDialog() != true) { Status = "Export annulé."; return; }
            var path = await _geo.ExportKmlAsync(Points.ToList(), dlg.FileName);
            Status = $"KML exporté : {path}";
        }
        catch (OperationCanceledException) { Status = "Annulé"; }
        catch (Exception ex) { _log.Error("geomac", "échec de l'export KML", ex); Status = "Erreur : " + ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ExportCsvAsync(CancellationToken ct)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            if (Points.Count == 0) { Status = "Aucun point géolocalisé à exporter."; return; }
            var dlg = new SaveFileDialog
            {
                Title = "Exporter en CSV",
                Filter = "CSV (*.csv)|*.csv",
                FileName = $"geomac_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
                InitialDirectory = SafeExportsDir()
            };
            if (dlg.ShowDialog() != true) { Status = "Export annulé."; return; }
            var path = await _geo.ExportCsvAsync(Points.ToList(), dlg.FileName);
            Status = $"CSV exporté : {path}";
        }
        catch (OperationCanceledException) { Status = "Annulé"; }
        catch (Exception ex) { _log.Error("geomac", "échec de l'export CSV", ex); Status = "Erreur : " + ex.Message; }
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
            Status = $"Point « {label} » supprimé.";
        }
        catch (OperationCanceledException) { Status = "Annulé"; }
        catch (Exception ex) { _log.Error("geomac", "échec de la suppression", ex); Status = "Erreur : " + ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void OpenMap(GeoPoint? p)
    {
        var pt = p ?? SelectedPoint;
        if (pt is null) { Status = "Sélectionnez d'abord un point."; return; }
        if (pt.Lat is null || pt.Lon is null) { Status = "Le point n'a pas de coordonnées."; return; }
        var lat = pt.Lat.Value.ToString(CultureInfo.InvariantCulture);
        var lon = pt.Lon.Value.ToString(CultureInfo.InvariantCulture);
        var url = $"https://www.openstreetmap.org/?mlat={lat}&mlon={lon}#map=17/{lat}/{lon}";
        Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        Status = $"OpenStreetMap ouvert pour « {pt.Label} ».";
    }

    private string SafeExportsDir()
    {
        try { return Path.Combine(_settings.EnsureDataDir(), "exports"); }
        catch { return Environment.CurrentDirectory; }
    }
}
