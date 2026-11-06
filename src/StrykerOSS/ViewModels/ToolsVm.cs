using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace StrykerOSS.ViewModels;

/// <summary>
/// Tools Manager VM: real per-tool detection (path + version) and real installs
/// (winget for nmap/hashcat, nuclei auto-downloader, manual guidance for msfvenom).
/// </summary>
public partial class ToolsVm : ObservableObject
{
    private readonly IToolsManager _tools;
    private readonly ILogger _log;

    public ObservableCollection<ToolStatus> Tools { get; } = new();

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Ready";
    [ObservableProperty] private string _toolsDir = "";

    public ToolsVm(IToolsManager tools, ILogger log)
    {
        _tools = tools;
        _log = log;
    }

    private bool CanInstall() => !IsBusy;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy) return;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        IsBusy = true;
        Status = "Probing nmap / nuclei / msfvenom / hashcat ...";
        try
        {
            var list = await Task.Run(() => _tools.StatusAsync());
            await App.Current.Dispatcher.InvokeAsync(() =>
            {
                Tools.Clear();
                foreach (var t in list) Tools.Add(t);
            });
            ToolsDir = _tools.ToolsDir();
            Status = $"{list.Count(t => t.Found)} of {list.Count} tools installed";
            _log.Info("tools-ui", $"refresh: {Status}");
        }
        catch (Exception ex)
        {
            _log.Error("tools-ui", "status check failed", ex);
            Status = "Error: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync(string? key)
    {
        if (IsBusy || string.IsNullOrWhiteSpace(key)) return;
        IsBusy = true;
        try
        {
            var progress = new Progress<string>(l =>
            {
                // keep the caption readable: last progress line, trimmed
                Status = l.Length > 140 ? l.Substring(0, 140) + "…" : l;
            });
            var ok = await Task.Run(() => _tools.InstallAsync(key, progress));
            Status = ok
                ? $"'{key}' installed successfully - refresh complete below."
                : $"'{key}' install did not complete - see last message above.";
            _log.Info("tools-ui", $"install '{key}' -> {ok}");
        }
        catch (Exception ex)
        {
            _log.Error("tools-ui", $"install '{key}' failed", ex);
            Status = "Error: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
        await LoadAsync();
    }

    [RelayCommand]
    private void OpenToolsDir()
    {
        try
        {
            var dir = _tools.ToolsDir();
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{dir}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _log.Error("tools-ui", "open tools folder failed", ex);
            Status = "Error: " + ex.Message;
        }
    }
}
