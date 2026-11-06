using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StrykerOSS.Core.Services;

namespace StrykerOSS.ViewModels;

/// <summary>
/// Console module VM: hosts one REAL interactive shell process
/// (powershell / cmd / wsl) with streamed output and live status badges.
/// </summary>
public partial class ConsoleVm : ObservableObject
{
    private readonly ITerminalService _terminal;
    private readonly ILogger _log;
    private ITerminalSession? _session;

    public ObservableCollection<string> Shells { get; } = new() { "powershell", "cmd", "wsl" };
    public ObservableCollection<string> Lines { get; } = new();

    [ObservableProperty] private string _selectedShell = "powershell";
    [ObservableProperty] private string _command = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private int? _pid;
    [ObservableProperty] private string _status = "Stopped";

    public ConsoleVm(ITerminalService terminal, ILogger log)
    {
        _terminal = terminal;
        _log = log;
    }

    private bool CanStart() => !IsRunning && !IsBusy;
    private bool CanSend() => IsRunning && !string.IsNullOrWhiteSpace(Command);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync(CancellationToken ct)
    {
        if (IsRunning || IsBusy) return;
        IsBusy = true;
        try
        {
            var shell = SelectedShell;
            AddLine($"Starting real {shell} process (elevated) ...");
            var progress = new Progress<string>(AddLine);
            var session = await _terminal.StartAsync(shell, null, progress, ct);

            session.Exited += () =>
            {
                var d = App.Current?.Dispatcher;
                if (d == null || d.CheckAccess()) OnExited(session);
                else d.Invoke(() => OnExited(session));
            };

            _session = session;
            IsRunning = true;
            Pid = session.Pid;
            Status = $"Running - {shell} pid {Pid}";
            _log.Info("console", $"{shell} terminal session started (pid {Pid})");
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled";
            AddLine("[start cancelled]");
        }
        catch (Exception ex)
        {
            _log.Error("console", "terminal start failed", ex);
            Status = "Error: " + ex.Message;
            AddLine($"[start failed: {ex.Message}]");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private void Send()
    {
        var s = _session;
        if (s == null || !s.IsAlive || string.IsNullOrWhiteSpace(Command)) return;
        var line = Command;
        Command = "";
        s.WriteLine(line);
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        var s = _session;
        if (s == null) return;
        IsBusy = true;
        _session = null;
        IsRunning = false;
        Pid = null;
        Status = "Stopped";
        try
        {
            await s.DisposeAsync(); // real Kill(entireProcessTree)
            AddLine("[session stopped - process tree killed]");
            _log.Info("console", "terminal session stopped");
        }
        catch (Exception ex)
        {
            _log.Warn("console", "terminal dispose failed: " + ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnCommandChanged(string value) => SendCommand.NotifyCanExecuteChanged();

    private void OnExited(ITerminalSession session)
    {
        if (!ReferenceEquals(_session, session)) return; // stale exit from an already-stopped session
        IsRunning = false;
        Pid = null;
        Status = "Stopped";
        _log.Info("console", "terminal session exited on its own");
    }

    private void AddLine(string line)
    {
        void Add()
        {
            Lines.Add(line);
            while (Lines.Count > 2000) Lines.RemoveAt(0); // cap 2000, drop oldest
        }
        var d = App.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) Add();
        else d.Invoke(Add);
    }
}
