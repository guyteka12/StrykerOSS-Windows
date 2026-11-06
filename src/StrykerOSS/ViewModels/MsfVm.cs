using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StrykerOSS.Core.Services;

namespace StrykerOSS.ViewModels;

public partial class MsfVm : ObservableObject
{
    private readonly IMetasploit _msf;
    private readonly ISettings _settings;
    private readonly ILogger _log;
    private int _loadingSessions;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Prêt";
    [ObservableProperty] private bool _detectDone;
    [ObservableProperty] private bool _detected;
    [ObservableProperty] private bool _showNotFound;
    [ObservableProperty] private string? _msfPathDisplay;

    [ObservableProperty] private string _rpcHost = "127.0.0.1";
    [ObservableProperty] private string _rpcPort = "55553";
    [ObservableProperty] private string _rpcUser = "msf";
    [ObservableProperty] private string _rpcPass = "";
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isOffline = true;

    [ObservableProperty] private string _searchTerm = "";
    [ObservableProperty] private MsfModuleRow? _selectedModule;
    [ObservableProperty] private bool _moduleSelected;
    [ObservableProperty] private string _runRhosts = "";
    [ObservableProperty] private string _runLhost = "";
    [ObservableProperty] private string _runLport = "";

    [ObservableProperty] private string _payload = "windows/x64/meterpreter/reverse_tcp";
    [ObservableProperty] private string _plHost = "";
    [ObservableProperty] private string _plPort = "4444";
    [ObservableProperty] private string _selectedFormat = "exe";
    [ObservableProperty] private string? _payloadOutPath;
    [ObservableProperty] private bool _payloadSaved;

    public ObservableCollection<MsfSession> Sessions { get; } = new();
    public ObservableCollection<MsfModuleRow> Modules { get; } = new();
    public ObservableCollection<string> ConsoleLines { get; } = new();
    public ObservableCollection<string> Formats { get; } = new() { "exe", "ps1", "raw", "dll", "csharp" };

    public MsfVm(IMetasploit msf, ISettings settings, ILogger log)
    {
        _msf = msf;
        _settings = settings;
        _log = log;

        var s = settings.Value;
        RpcHost = s.MsfRpcHost;
        RpcPort = s.MsfRpcPort.ToString();
        RpcUser = s.MsfRpcUser;
        RpcPass = s.MsfRpcPass;

        _msf.SessionsChanged += OnSessionsChanged;
    }

    partial void OnIsConnectedChanged(bool value) => IsOffline = !value;
    partial void OnSelectedModuleChanged(MsfModuleRow? value) => ModuleSelected = value is not null;

    // ---- detection ----

    [RelayCommand]
    private async Task DetectAsync(CancellationToken ct)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            Status = "Détection du Metasploit Framework...";
            Detected = await _msf.DetectAsync();
            DetectDone = true;
            ShowNotFound = !Detected;
            MsfPathDisplay = _msf.MsfPath;
            Status = Detected ? $"Metasploit trouvé : {_msf.MsfPath}" : "Metasploit introuvable - installez-le pour continuer.";
            _log.Info("msf", Status);
        }
        catch (OperationCanceledException) { Status = "Annulé"; }
        catch (Exception ex) { _log.Error("msf", "detect failed", ex); Status = "Erreur : " + ex.Message; }
        finally { IsBusy = false; }
    }

    // ---- MSGRPC connection ----

    [RelayCommand]
    private void SaveRpc()
    {
        var s = _settings.Value;
        s.MsfRpcHost = string.IsNullOrWhiteSpace(RpcHost) ? "127.0.0.1" : RpcHost.Trim();
        if (int.TryParse(RpcPort, out var p) && p > 0 && p < 65536) s.MsfRpcPort = p;
        var user = RpcUser?.Trim();
        s.MsfRpcUser = string.IsNullOrEmpty(user) ? "msf" : user;
        s.MsfRpcPass = RpcPass ?? "";
        _settings.Save();
        RpcPort = s.MsfRpcPort.ToString();
        Status = $"Paramètres MSGRPC enregistrés ({s.MsfRpcHost}:{s.MsfRpcPort}).";
        _log.Info("msf", "RPC settings saved");
    }

    [RelayCommand]
    private async Task ConnectRpcAsync(CancellationToken ct)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            Status = "Connexion à msfrpcd...";
            var ok = await _msf.ConnectRpcAsync(new Progress<string>(l =>
                App.Current.Dispatcher.Invoke(() => { AddConsole(l); })), ct);
            IsConnected = ok;
            if (ok)
            {
                Status = "MSGRPC connecté - chargement des sessions...";
                await LoadSessionsAsync();
            }
            else
            {
                Status = "Échec de connexion MSGRPC - vérifiez les identifiants et que msfrpcd est en cours d'exécution.";
            }
        }
        catch (OperationCanceledException) { Status = "Annulé"; }
        catch (Exception ex) { _log.Error("msf", "connect failed", ex); Status = "Erreur : " + ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        if (IsBusy) return;
        try
        {
            await _msf.DisconnectAsync();
            IsConnected = false;
            Status = "MSGRPC déconnecté.";
        }
        catch (Exception ex) { _log.Error("msf", "disconnect failed", ex); Status = "Erreur : " + ex.Message; }
    }

    // ---- sessions ----

    [RelayCommand]
    private async Task RefreshSessionsAsync(CancellationToken ct)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            Status = "Chargement des sessions...";
            await LoadSessionsAsync();
        }
        catch (OperationCanceledException) { Status = "Annulé"; }
        catch (Exception ex) { _log.Error("msf", "session refresh failed", ex); Status = "Erreur : " + ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task KillSessionAsync(int? id)
    {
        if (IsBusy || id is not int sid) return;
        IsBusy = true;
        try
        {
            var ok = await _msf.KillSessionAsync(sid);
            Status = ok ? $"Session {sid} terminée." : $"Échec de l'arrêt de la session {sid}.";
            if (ok) await LoadSessionsAsync();
        }
        catch (OperationCanceledException) { Status = "Annulé"; }
        catch (Exception ex) { _log.Error("msf", "kill session failed", ex); Status = "Erreur : " + ex.Message; }
        finally { IsBusy = false; }
    }

    private async Task LoadSessionsAsync()
    {
        if (Interlocked.CompareExchange(ref _loadingSessions, 1, 0) != 0) return;
        try
        {
            var list = await _msf.ListSessionsAsync();
            App.Current.Dispatcher.Invoke(() =>
            {
                Sessions.Clear();
                foreach (var s in list) Sessions.Add(s);
            });
            Status = $"{list.Count} session(s) ouverte(s).";
        }
        finally
        {
            Interlocked.Exchange(ref _loadingSessions, 0);
        }
    }

    private void OnSessionsChanged()
    {
        // sessions changed on the msfrpcd side (e.g. a module opened one) - reload, guarded against recursion
        _ = Task.Run(async () =>
        {
            try { await LoadSessionsAsync(); }
            catch (Exception ex) { _log.Warn("msf", $"auto session refresh: {ex.Message}"); }
        });
    }

    // ---- modules ----

    [RelayCommand]
    private async Task SearchAsync(CancellationToken ct)
    {
        if (IsBusy) return;
        if (!IsConnected) { Status = "Connectez-vous d'abord à msfrpcd."; return; }
        if (string.IsNullOrWhiteSpace(SearchTerm)) { Status = "Saisissez un terme de recherche de module."; return; }
        IsBusy = true;
        try
        {
            Status = $"Recherche de modules pour '{SearchTerm.Trim()}'...";
            var list = await _msf.SearchModulesAsync(SearchTerm.Trim(), 50);
            App.Current.Dispatcher.Invoke(() =>
            {
                Modules.Clear();
                foreach (var m in list) Modules.Add(new MsfModuleRow(m));
            });
            Status = $"{list.Count} module(s) trouvé(s).";
        }
        catch (OperationCanceledException) { Status = "Annulé"; }
        catch (Exception ex) { _log.Error("msf", "module search failed", ex); Status = "Erreur : " + ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task RecentExploitsAsync(CancellationToken ct)
    {
        if (IsBusy) return;
        if (!IsConnected) { Status = "Connectez-vous d'abord à msfrpcd."; return; }
        IsBusy = true;
        try
        {
            Status = "Chargement de la liste des exploits depuis msfrpcd...";
            var list = await _msf.RecentExploitsAsync(50);
            App.Current.Dispatcher.Invoke(() =>
            {
                Modules.Clear();
                foreach (var m in list) Modules.Add(new MsfModuleRow(m));
            });
            Status = $"{list.Count} exploit(s) disponible(s).";
        }
        catch (OperationCanceledException) { Status = "Annulé"; }
        catch (Exception ex) { _log.Error("msf", "exploit list failed", ex); Status = "Erreur : " + ex.Message; }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void RunModule(MsfModuleRow? row)
    {
        if (row is null) return;
        SelectedModule = row;
        Status = $"Module '{row.FullName}' sélectionné - définissez les options puis exécutez.";
    }

    [RelayCommand]
    private async Task ExecuteModuleAsync(CancellationToken ct)
    {
        if (IsBusy || SelectedModule is null) return;
        if (!IsConnected) { Status = "Connectez-vous d'abord à msfrpcd."; return; }
        IsBusy = true;
        try
        {
            var module = SelectedModule.FullName;
            var opts = new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(RunRhosts)) opts["RHOSTS"] = RunRhosts.Trim();
            if (!string.IsNullOrWhiteSpace(RunLhost)) opts["LHOST"] = RunLhost.Trim();
            if (!string.IsNullOrWhiteSpace(RunLport)) opts["LPORT"] = RunLport.Trim();

            AddConsole($"> use {module}");
            foreach (var kv in opts) AddConsole($"> set {kv.Key} {kv.Value}");
            AddConsole("> run");
            Status = $"Exécution de {module} via la console msfrpcd (lecture de la sortie)...";

            var output = await _msf.RunModuleAsync(module, opts,
                new Progress<string>(l => App.Current.Dispatcher.Invoke(() => AddConsole(l))), ct);

            foreach (var line in output.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
                AddConsole(line);
            Status = $"Exécution console du module {module} terminée.";
        }
        catch (OperationCanceledException) { Status = "Annulé"; }
        catch (Exception ex) { _log.Error("msf", "module run failed", ex); Status = "Erreur : " + ex.Message; }
        finally { IsBusy = false; }
    }

    // ---- payload generator ----

    [RelayCommand]
    private async Task GeneratePayloadAsync(CancellationToken ct)
    {
        if (IsBusy) return;
        if (string.IsNullOrWhiteSpace(Payload)) { Status = "Saisissez un nom de payload."; return; }
        if (!int.TryParse(PlPort, out var lport)) { Status = "LPORT invalide."; return; }
        IsBusy = true;
        PayloadSaved = false;
        try
        {
            var safeName = Payload.Trim().Replace('/', '_');
            var outPath = Path.Combine(_settings.EnsureDataDir(), "payloads", $"{safeName}.{SelectedFormat}");
            PayloadOutPath = outPath;
            Status = $"Génération de {Payload.Trim()} ({SelectedFormat}) avec msfvenom...";

            var result = await _msf.GeneratePayloadAsync(Payload.Trim(), PlHost.Trim(), lport, SelectedFormat,
                outPath, new Progress<string>(l => App.Current.Dispatcher.Invoke(() => AddConsole(l))), ct);

            PayloadOutPath = result;
            PayloadSaved = File.Exists(result);
            Status = PayloadSaved ? $"Payload enregistré : {result}" : "Échec de la génération du payload - aucun fichier de sortie.";
        }
        catch (OperationCanceledException) { Status = "Annulé"; }
        catch (Exception ex) { _log.Error("msf", "payload generation failed", ex); Status = "Erreur : " + ex.Message; }
        finally { IsBusy = false; }
    }

    private void AddConsole(string line)
    {
        if (string.IsNullOrEmpty(line)) return;
        if (ConsoleLines.Count > 500) ConsoleLines.RemoveAt(0);
        ConsoleLines.Add(line);
    }
}

/// <summary>Row wrapper exposing rank/type badge flags for the module list template.</summary>
public sealed record MsfModuleRow(MsfModule Module)
{
    public string FullName => Module.FullName;
    public string Name => Module.Name;
    public string Rank => Module.Rank;
    public string Type => Module.Type;
    public string Description => Module.Description ?? "";
    public bool IsHighRank => Rank.Equals("excellent", StringComparison.OrdinalIgnoreCase)
                           || Rank.Equals("great", StringComparison.OrdinalIgnoreCase);
    public bool IsManualRank => Rank.Equals("manual", StringComparison.OrdinalIgnoreCase);
    public bool IsOtherRank => !IsHighRank && !IsManualRank;
}
