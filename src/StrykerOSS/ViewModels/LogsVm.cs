using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace StrykerOSS.ViewModels;

/// <summary>
/// Logs VM: live tail of the real ILogger event stream (in-memory + logs.txt
/// backing store stays untouched), level filter, and real export to
/// {DataDir}/exports/logs_export.txt.
/// </summary>
public partial class LogsVm : ObservableObject
{
    private readonly ILogger _log;
    private readonly ISettings _settings;
    private readonly object _sync = new();
    private readonly List<LogEntry> _all = new();

    public ObservableCollection<string> Levels { get; } = new() { "ALL", "INFO", "WARN", "ERROR" };
    public ObservableCollection<string> Lines { get; } = new();

    [ObservableProperty] private string _selectedLevel = "ALL";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Prêt";

    public LogsVm(ILogger log, ISettings settings)
    {
        _log = log;
        _settings = settings;

        lock (_sync)
        {
            _all.AddRange(log.Recent(3000));
        }
        Rebuild();

        log.Logged += OnLogged;
    }

    partial void OnSelectedLevelChanged(string value) => Rebuild();

    private void OnLogged(LogEntry e)
    {
        lock (_sync)
        {
            _all.Add(e);
            if (_all.Count > 3000) _all.RemoveRange(0, _all.Count - 3000);
        }

        void Add()
        {
            if (!Passes(e)) return;
            Lines.Add(Format(e));
            while (Lines.Count > 3000) Lines.RemoveAt(0);
        }
        var d = App.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) Add();
        else d.Invoke(Add);
    }

    private bool Passes(LogEntry e)
        => SelectedLevel == "ALL" || e.Level.Equals(SelectedLevel, StringComparison.OrdinalIgnoreCase);

    private void Rebuild()
    {
        var d = App.Current?.Dispatcher;
        if (d != null && !d.CheckAccess()) { d.Invoke(Rebuild); return; }

        Lines.Clear();
        lock (_sync)
        {
            foreach (var e in _all)
                if (Passes(e)) Lines.Add(Format(e));
        }
        while (Lines.Count > 3000) Lines.RemoveAt(0);
        Status = $"{Lines.Count} lignes affichées";
    }

    private static string Format(LogEntry e) => $"[{e.Time:HH:mm:ss}] [{e.Level}] {e.Source}: {e.Message}";

    [RelayCommand]
    private void ClearView()
    {
        Lines.Clear();
        Status = "Affichage effacé (fichier de journal et historique en mémoire intacts)";
    }

    [RelayCommand]
    private void Export()
    {
        try
        {
            var dir = Path.Combine(_settings.Value.DataDir, "exports");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "logs_export.txt");

            List<string> snapshot;
            lock (_sync)
            {
                snapshot = _all.Where(Passes).Select(Format).ToList();
            }
            File.WriteAllLines(path, snapshot);

            Status = $"{snapshot.Count} lignes exportées -> {path}";
            _log.Info("logs-ui", $"{snapshot.Count} lignes de journal exportées vers {path}");
        }
        catch (Exception ex)
        {
            _log.Error("logs-ui", "échec de l'export", ex);
            Status = "Échec de l'export : " + ex.Message;
        }
    }
}
