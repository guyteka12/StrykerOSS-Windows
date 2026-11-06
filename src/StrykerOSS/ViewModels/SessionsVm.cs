using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StrykerOSS.Core.Services;

namespace StrykerOSS.ViewModels;

public partial class SessionsVm : ObservableObject
{
    private readonly IDbService _db;
    private readonly IMetasploit _msf;
    private readonly ILogger _log;

    public ObservableCollection<SessionRecord> Sessions { get; } = new();
    public ObservableCollection<MsfSession> MsfSessions { get; } = new();
    public ObservableCollection<string> Kinds { get; } = new() { "manual", "nmap", "nuclei", "msf", "terminal" };

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Ready";
    [ObservableProperty] private string _newSessionName = "";
    [ObservableProperty] private string _newSessionKind = "manual";
    [ObservableProperty] private string _newSessionTarget = "";
    [ObservableProperty] private bool _msfConnected;

    public SessionsVm(IDbService db, IMetasploit msf, ILogger log)
    {
        _db = db;
        _msf = msf;
        _log = log;

        MsfConnected = _msf.IsConnected;
        _msf.SessionsChanged += OnMsfChanged;
        _ = LoadCoreAsync();
    }

    private void OnMsfChanged()
    {
        App.Current.Dispatcher.Invoke(async () =>
        {
            MsfConnected = _msf.IsConnected;
            await RefreshMsfAsync();
        });
    }

    // ---------------- local sessions ----------------

    private async Task LoadCoreAsync()
    {
        try
        {
            var list = await _db.GetSessionsAsync(100);
            await App.Current.Dispatcher.InvokeAsync(() =>
            {
                Sessions.Clear();
                foreach (var s in list) Sessions.Add(s);
            });
            MsfConnected = _msf.IsConnected;
            Status = $"{list.Count} session(s) on record";
        }
        catch (Exception ex)
        {
            _log.Error("sessions", "reload failed", ex);
            Status = "Error: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try { await LoadCoreAsync(); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task StartSessionAsync()
    {
        if (IsBusy) return;
        if (string.IsNullOrWhiteSpace(NewSessionName)) { Status = "Session name is required."; return; }
        IsBusy = true;
        try
        {
            var kind = string.IsNullOrWhiteSpace(NewSessionKind) ? "manual" : NewSessionKind.Trim();
            var s = await _db.StartSessionAsync(NewSessionName.Trim(), kind, NewSessionTarget.Trim());
            _log.Info("sessions", $"session #{s.Id} started ({s.Kind}) target={s.Target}");
            NewSessionName = "";
            NewSessionTarget = "";
            await LoadCoreAsync();
            Status = $"Session #{s.Id} started.";
        }
        catch (Exception ex)
        {
            _log.Error("sessions", "start failed", ex);
            Status = "Error: " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task EndSessionAsync(SessionRecord? s)
    {
        if (s is null || s.Ended != null) return;
        try
        {
            await _db.EndSessionAsync(s.Id);
            _log.Info("sessions", $"session #{s.Id} ended");
            await LoadCoreAsync();
            Status = $"Session #{s.Id} ended.";
        }
        catch (Exception ex)
        {
            _log.Error("sessions", "end failed", ex);
            Status = "Error: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task DeleteSessionAsync(SessionRecord? s)
    {
        if (s is null) return;
        try
        {
            if (_db is DbService concrete)
            {
                await concrete.DeleteSessionAsync(s.Id);
                _log.Info("sessions", $"session #{s.Id} deleted");
                await LoadCoreAsync();
                Status = $"Session #{s.Id} deleted.";
            }
            else
            {
                Status = "Session delete is not supported by the current provider.";
            }
        }
        catch (Exception ex)
        {
            _log.Error("sessions", "delete failed", ex);
            Status = "Error: " + ex.Message;
        }
    }

    // ---------------- metasploit rpc ----------------

    [RelayCommand]
    private async Task ConnectMsfAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            Status = "Connecting to MSF RPC...";
            var ok = await _msf.ConnectRpcAsync();
            MsfConnected = _msf.IsConnected;
            if (ok)
            {
                _log.Info("sessions", "MSF RPC connected");
                await RefreshMsfAsync();
            }
            else
            {
                Status = "MSF RPC connect failed - is msfrpcd running with the configured credentials?";
                _log.Warn("sessions", "MSF RPC connect failed");
            }
        }
        catch (Exception ex)
        {
            _log.Error("sessions", "msf connect failed", ex);
            Status = "Error: " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task RefreshMsfAsync()
    {
        if (!_msf.IsConnected)
        {
            MsfConnected = false;
            Status = "MSF RPC not connected.";
            return;
        }
        try
        {
            var list = await _msf.ListSessionsAsync();
            await App.Current.Dispatcher.InvokeAsync(() =>
            {
                MsfSessions.Clear();
                foreach (var s in list) MsfSessions.Add(s);
                Status = $"MSF RPC: {list.Count} live session(s)";
            });
        }
        catch (Exception ex)
        {
            _log.Error("sessions", "msf list failed", ex);
            await App.Current.Dispatcher.InvokeAsync(() => Status = "MSF error: " + ex.Message);
        }
    }

    [RelayCommand]
    private async Task KillMsfAsync(MsfSession? s)
    {
        if (s is null) return;
        try
        {
            var ok = await _msf.KillSessionAsync(s.Id);
            _log.Warn("sessions", ok ? $"MSF session {s.Id} killed" : $"MSF session {s.Id} kill failed");
            Status = ok ? $"MSF session {s.Id} killed." : $"Failed to kill MSF session {s.Id}.";
            await RefreshMsfAsync();
        }
        catch (Exception ex)
        {
            _log.Error("sessions", "msf kill failed", ex);
            Status = "Error: " + ex.Message;
        }
    }
}
