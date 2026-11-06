using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace StrykerOSS.ViewModels;

/// <summary>Row model for the handshakes list (DB records merged with live analysis results).</summary>
public sealed class HandshakeRow
{
    public int Id { get; init; }
    public string FileName { get; init; } = "";
    public string FilePath { get; init; } = "";
    public string Ssid { get; init; } = "-";
    public string Bssid { get; init; } = "-";
    public int EapolFrames { get; init; }
    public bool HasHandshake { get; init; }
    public string Clients { get; init; } = "-";
    public DateTime Analyzed { get; init; }
    public HandshakeInfo Info { get; init; } = new(0, "", null, null, 0, false, new List<string>(), DateTime.Now);

    public string MetaLine => $"ssid: {Ssid}   bssid: {Bssid}   analysé le : {Analyzed:yyyy-MM-dd HH:mm}";
    public string ClientsLine => "clients : " + Clients;
    public string FramesText => $"{EapolFrames} EAPOL";
    public Visibility CompleteVis => HasHandshake ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IncompleteVis => HasHandshake ? Visibility.Collapsed : Visibility.Visible;
}

public partial class HandshakesVm : ObservableObject
{
    private readonly IHandshakeService _hs;
    private readonly IDbService _db;
    private readonly ILogger _log;
    private readonly List<HandshakeInfo> _session = new();   // analyzed in this session (merged with DB on refresh)

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Prêt";
    [ObservableProperty] private string _folderPath = "";

    public ObservableCollection<HandshakeRow> Items { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();

    public HandshakesVm(IHandshakeService hs, IDbService db, ILogger log)
    {
        _hs = hs;
        _db = db;
        _log = log;
        LoadCommand.Execute(null);                             // initial load (errors handled inside)
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try { await RefreshAsync(); }
        catch (OperationCanceledException) { Status = "Annulé"; }
        catch (Exception ex) { _log.Error("handshakes", "Échec du chargement", ex); Status = "Erreur : " + ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task AnalyzeFileAsync()
    {
        if (IsBusy) return;
        var dlg = new OpenFileDialog
        {
            Title = "Ouvrir une capture de paquets",
            Filter = "Captures de paquets (*.pcap;*.cap;*.pcapng)|*.pcap;*.cap;*.pcapng|Tous les fichiers (*.*)|*.*",
            CheckFileExists = true
        };
        if (dlg.ShowDialog() != true) return;
        IsBusy = true;
        try
        {
            Status = $"Analyse de {Path.GetFileName(dlg.FileName)}...";
            var info = await _hs.AnalyzeAsync(dlg.FileName);
            _session.Add(info);
            Log($"analysé {info.FilePath} : eapol={info.EapolFrames} handshake={(info.HasHandshake ? "COMPLET" : "INCOMPLET")}");
            await RefreshAsync();
            Status = info.HasHandshake
                ? $"Handshake COMPLET pour {(info.Ssid ?? info.Bssid)} ({info.EapolFrames} trames EAPOL)"
                : $"Aucun handshake complet dans {Path.GetFileName(dlg.FileName)} ({info.EapolFrames} trames EAPOL)";
        }
        catch (OperationCanceledException) { Status = "Annulé"; }
        catch (Exception ex) { _log.Error("handshakes", "Échec de l'analyse", ex); Status = "Erreur : " + ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task AnalyzeFolderAsync()
    {
        if (IsBusy) return;
        if (string.IsNullOrWhiteSpace(FolderPath) || !Directory.Exists(FolderPath))
        {
            Status = "Saisissez d'abord un chemin de dossier valide.";
            return;
        }
        IsBusy = true;
        try
        {
            Status = "Analyse du dossier...";
            var prog = new Progress<string>(Log);
            var found = await _hs.AnalyzeDirectoryAsync(FolderPath, prog);
            _session.AddRange(found);
            await RefreshAsync();
            Status = $"Analysé {found.Count} fichier(s) - {found.Count(h => h.HasHandshake)} handshake(s) complet(s)";
        }
        catch (OperationCanceledException) { Status = "Annulé"; }
        catch (Exception ex) { _log.Error("handshakes", "Échec de l'analyse du dossier", ex); Status = "Erreur : " + ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        if (Items.Count == 0) { Status = "Rien à exporter - analysez d'abord une capture."; return; }
        var dlg = new SaveFileDialog
        {
            Title = "Exporter les handshakes en CSV",
            Filter = "CSV (*.csv)|*.csv",
            FileName = "handshakes.csv"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var rows = Items.Select(r => r.Info).ToList();
            var path = await _hs.ExportCsvAsync(rows, dlg.FileName);
            Log($"csv exporté -> {path}");
            Status = "CSV exporté : " + path;
        }
        catch (Exception ex) { _log.Error("handshakes", "Échec de l'export CSV", ex); Status = "Erreur : " + ex.Message; }
    }

    [RelayCommand]
    private async Task ExportHashAsync(HandshakeRow? row)
    {
        if (row is null) return;
        var nameBase = row.Ssid == "-" ? row.Bssid : row.Ssid;
        var dlg = new SaveFileDialog
        {
            Title = "Exporter vers Hashcat 22000",
            Filter = "Hashcat 22000 (*.txt)|*.txt|Tous les fichiers (*.*)|*.*",
            FileName = $"{nameBase}.22000.txt"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var outDir = Path.GetDirectoryName(dlg.FileName);
            if (string.IsNullOrEmpty(outDir)) { Status = "Dossier de sortie invalide."; return; }
            var path = await _hs.ExportHashcat22000Async(row.Info, outDir);
            Log($"22000 exporté -> {path}");
            Status = "Hashcat 22000 exporté : " + path;
        }
        catch (OperationCanceledException) { Status = "Annulé"; }
        catch (Exception ex) { _log.Error("handshakes", "Échec de l'export 22000", ex); Status = "Erreur : " + ex.Message; }
    }

    [RelayCommand]
    private async Task DeleteAsync(HandshakeRow? row)
    {
        if (row is null) return;
        try
        {
            if (row.Id > 0) await _db.DeleteHandshakeAsync(row.Id);
            _session.RemoveAll(s => Norm(s.FilePath) == Norm(row.FilePath));
            Log($"supprimé {row.FileName}");
            await RefreshAsync();
            Status = "Supprimé " + row.FileName;
        }
        catch (Exception ex) { _log.Error("handshakes", "Échec de la suppression", ex); Status = "Erreur : " + ex.Message; }
    }

    /// <summary>DB records merged with just-analyzed session items (dedup by real path).</summary>
    private async Task RefreshAsync()
    {
        var dbRows = await _db.GetHandshakesAsync();
        var merged = new List<HandshakeInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in dbRows)
            if (seen.Add(Norm(r.FilePath))) merged.Add(r);
        foreach (var s in _session)
            if (seen.Add(Norm(s.FilePath))) merged.Add(s);

        var rows = merged.Select(Row).ToList();
        App.Current.Dispatcher.Invoke(() =>
        {
            Items.Clear();
            foreach (var r in rows) Items.Add(r);
        });
        Status = $"{rows.Count} capture(s) - {rows.Count(r => r.HasHandshake)} avec handshake complet";
    }

    private static HandshakeRow Row(HandshakeInfo h) => new()
    {
        Id = h.Id,
        FileName = Path.GetFileName(h.FilePath),
        FilePath = h.FilePath,
        Ssid = string.IsNullOrWhiteSpace(h.Ssid) ? "-" : h.Ssid!,
        Bssid = string.IsNullOrWhiteSpace(h.Bssid) ? "-" : h.Bssid!,
        EapolFrames = h.EapolFrames,
        HasHandshake = h.HasHandshake,
        Clients = h.ClientMacs.Count == 0 ? "-" : string.Join(", ", h.ClientMacs),
        Analyzed = h.Analyzed,
        Info = h
    };

    private void Log(string msg)
    {
        _log.Info("handshakes", msg);
        var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        App.Current.Dispatcher.Invoke(() =>
        {
            LogLines.Add(line);
            while (LogLines.Count > 400) LogLines.RemoveAt(0);
        });
    }

    private static string Norm(string p) => Path.GetFullPath(p).ToLowerInvariant();
}
