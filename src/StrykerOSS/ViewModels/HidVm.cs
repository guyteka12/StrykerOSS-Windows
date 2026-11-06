using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace StrykerOSS.ViewModels;

public partial class HidVm : ObservableObject
{
    private readonly IHidService _hid;
    private readonly ISettings _settings;
    private readonly ILogger _log;
    private CancellationTokenSource? _runCts;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Ready";
    [ObservableProperty] private string _scriptPath = "";
    [ObservableProperty] private string _scriptSummary = "No script loaded.";
    [ObservableProperty] private bool _confirmExecute;

    public ObservableCollection<string> LogLines { get; } = new();

    public HidVm(IHidService hid, ISettings settings, ILogger log)
    {
        _hid = hid;
        _settings = settings;
        _log = log;
    }

    [RelayCommand]
    private async Task OpenScriptAsync()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Open DuckyScript",
            Filter = "DuckyScript (*.duck;*.txt)|*.duck;*.txt|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dlg.ShowDialog() != true) return;
        ScriptPath = dlg.FileName;
        await InspectAsync();
    }

    [RelayCommand]
    private async Task RunAsync()
    {
        if (IsBusy) return;
        if (string.IsNullOrWhiteSpace(ScriptPath) || !File.Exists(ScriptPath))
        {
            Status = "Open a .duck script first.";
            return;
        }
        if (!ConfirmExecute)
        {
            Status = "Tick the confirmation box first - keys go to the FOCUSED window.";
            return;
        }
        IsBusy = true;
        _runCts = new CancellationTokenSource();
        try
        {
            Log($"> run {ScriptPath} - focus the safe target window NOW");
            _log.Info("hid", $"run requested: {ScriptPath}");
            await _hid.RunAsync(ScriptPath, new Progress<string>(Log), _runCts.Token);
            Status = "Script finished.";
        }
        catch (OperationCanceledException)
        {
            Status = "Stopped.";
            Log("> stopped by user");
        }
        catch (Exception ex)
        {
            _log.Error("hid", "run failed", ex);
            Status = "Error: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            _runCts?.Dispose();
            _runCts = null;
        }
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        try
        {
            _runCts?.Cancel();
            await _hid.StopAsync();
            Status = "Stopped.";
        }
        catch (Exception ex)
        {
            _log.Error("hid", "stop failed", ex);
            Status = "Error: " + ex.Message;
        }
    }

    /// <summary>Writes a SAFE educational sample script into DataDir/captures.</summary>
    [RelayCommand]
    private async Task MakeSampleAsync()
    {
        try
        {
            var dir = Path.Combine(_settings.EnsureDataDir(), "captures");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "hid_sample.duck");
            var lines = new[]
            {
                "REM ============================================",
                "REM StrykerOSS HID sample script (educational)",
                "REM Focus a safe window (e.g. Notepad) first.",
                "REM This only types one harmless line.",
                "REM ============================================",
                "",
                "DELAY 2000",
                "STRING Hello from StrykerOSS HID module (educational)",
                "ENTER"
            };
            await File.WriteAllTextAsync(path, string.Join(Environment.NewLine, lines) + Environment.NewLine);
            ScriptPath = path;
            Log("sample written -> " + path);
            _log.Info("hid", "safe sample script written: " + path);
            await InspectAsync();
            Status = "Sample script created: " + path;
        }
        catch (Exception ex)
        {
            _log.Error("hid", "sample write failed", ex);
            Status = "Error: " + ex.Message;
        }
    }

    private async Task InspectAsync()
    {
        if (string.IsNullOrWhiteSpace(ScriptPath) || !File.Exists(ScriptPath)) return;
        try
        {
            var info = await _hid.ParseAsync(ScriptPath);
            ScriptSummary = $"{info.Lines} lines | {info.StringChars} string chars | estimated duration {Fmt(info.Estimated)}";
            Status = "Script parsed: " + info.Name;
        }
        catch (Exception ex)
        {
            _log.Error("hid", "parse failed", ex);
            ScriptSummary = "Could not parse script.";
            Status = "Error: " + ex.Message;
        }
    }

    private void Log(string msg)
    {
        // "> COMMAND" echoes come from the service via IProgress - keep them UI-only
        if (!msg.StartsWith("> ")) _log.Info("hid", msg);
        App.Current.Dispatcher.Invoke(() =>
        {
            LogLines.Add(msg);
            while (LogLines.Count > 500) LogLines.RemoveAt(0);
        });
    }

    private static string Fmt(TimeSpan t) =>
        t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds:00}s" : $"{t.TotalSeconds:F1}s";
}
