using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace StrykerOSS.ViewModels;

public partial class MacChangerVm : ObservableObject
{
    private readonly IMacChanger _mac;
    private readonly IDbService _db;
    private readonly ILogger _log;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Ready";
    [ObservableProperty] private MacChangerAdapter? _selectedAdapter;
    [ObservableProperty] private string _macText = "";
    [ObservableProperty] private string _applyConfirm = "";
    [ObservableProperty] private string _profileName = "";
    [ObservableProperty] private MacProfile? _selectedProfile;
    [ObservableProperty] private string _currentMac = "-";
    [ObservableProperty] private string _configuredMac = "-";
    [ObservableProperty] private bool _hasOverride;
    [ObservableProperty] private bool _isWireless;

    public ObservableCollection<MacChangerAdapter> Adapters { get; } = new();
    public ObservableCollection<MacProfile> Profiles { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();

    public MacChangerVm(IMacChanger mac, IDbService db, ILogger log)
    {
        _mac = mac;
        _db = db;
        _log = log;
        _ = LoadAsync();
    }

    // ---- change notifications for CanExecute ----

    partial void OnIsBusyChanged(bool value) => NotifyAllCommands();

    partial void OnSelectedAdapterChanged(MacChangerAdapter? value)
    {
        CurrentMac = string.IsNullOrWhiteSpace(value?.CurrentMac) ? "-" : value.CurrentMac;
        ConfiguredMac = string.IsNullOrWhiteSpace(value?.PermanentMac) ? "unknown" : value.PermanentMac;
        HasOverride = !string.IsNullOrWhiteSpace(value?.CurrentMac)
                      && !string.IsNullOrWhiteSpace(value?.PermanentMac)
                      && !string.Equals(value.CurrentMac, value.PermanentMac, StringComparison.OrdinalIgnoreCase);
        IsWireless = value?.Wireless ?? false;
        ApplyCommand.NotifyCanExecuteChanged();
        SaveProfileCommand.NotifyCanExecuteChanged();
    }

    partial void OnMacTextChanged(string value) => NotifyMacDependent();
    partial void OnApplyConfirmChanged(string value) => ApplyCommand.NotifyCanExecuteChanged();
    partial void OnProfileNameChanged(string value) => SaveProfileCommand.NotifyCanExecuteChanged();

    partial void OnSelectedProfileChanged(MacProfile? value)
    {
        ApplyProfileCommand.NotifyCanExecuteChanged();
        DeleteProfileCommand.NotifyCanExecuteChanged();
    }

    private void NotifyMacDependent()
    {
        ApplyCommand.NotifyCanExecuteChanged();
        SaveProfileCommand.NotifyCanExecuteChanged();
    }

    private void NotifyAllCommands()
    {
        ApplyCommand.NotifyCanExecuteChanged();
        SaveProfileCommand.NotifyCanExecuteChanged();
        ApplyProfileCommand.NotifyCanExecuteChanged();
        DeleteProfileCommand.NotifyCanExecuteChanged();
    }

    // ---- validation ----

    private static string NormalizeMac(string s)
        => (s ?? "").Replace(":", "").Replace("-", "").Trim().ToUpperInvariant();

    private static bool IsMacValid(string s)
    {
        var c = NormalizeMac(s);
        return c.Length == 12 && c.All(Uri.IsHexDigit);
    }

    private static string FormatMac(string mac)
    {
        var hex = NormalizeMac(mac);
        return hex.Length == 12
            ? string.Join(":", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)))
            : mac;
    }

    private bool CanApply() => !IsBusy && SelectedAdapter != null && IsMacValid(MacText) && ApplyConfirm == "APPLY";
    private bool CanSaveProfile() => !IsBusy && SelectedAdapter != null && IsMacValid(MacText) && !string.IsNullOrWhiteSpace(ProfileName);
    private bool CanUseSelectedProfile() => !IsBusy && SelectedProfile != null;

    // ---- commands ----

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        Status = "Enumerating adapters and profiles...";
        try
        {
            var adapters = await _mac.ListAdaptersAsync();
            var profiles = await _mac.GetProfilesAsync();
            App.Current.Dispatcher.Invoke(() =>
            {
                var previous = SelectedAdapter?.InterfaceGuid;
                Adapters.Clear();
                foreach (var a in adapters) Adapters.Add(a);
                SelectedAdapter = Adapters.FirstOrDefault(a => a.InterfaceGuid == previous) ?? Adapters.FirstOrDefault();
                Profiles.Clear();
                foreach (var p in profiles) Profiles.Add(p);
            });
            Status = $"Ready - {Adapters.Count} adapter(s), {Profiles.Count} profile(s)";
            _log.Info("macchanger", $"listed {Adapters.Count} adapter(s), {Profiles.Count} profile(s)");
        }
        catch (Exception ex)
        {
            _log.Error("macchanger", "loading adapters failed", ex);
            Status = "Error: " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        if (IsBusy || SelectedAdapter is null || ApplyConfirm != "APPLY" || !IsMacValid(MacText)) return;
        var adapter = SelectedAdapter;
        string mac = NormalizeMac(MacText);
        IsBusy = true;
        Status = $"Applying MAC {FormatMac(mac)} on '{adapter.Description}'...";
        try
        {
            var progress = new Progress<string>(s => App.Current.Dispatcher.Invoke(() => AddLog(s)));
            bool ok = await _mac.SetMacAsync(adapter.InterfaceGuid, mac, progress);
            await ReloadAdaptersAsync();
            Status = ok
                ? $"MAC applied: {FormatMac(mac)} (adapter restarted)."
                : "Registry updated, but the change was not confirmed in time - see the log.";
            _log.Info("macchanger", $"set {FormatMac(mac)} on {adapter.InterfaceGuid}: {ok}");
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled";
            _log.Warn("macchanger", "apply cancelled");
        }
        catch (Exception ex)
        {
            _log.Error("macchanger", "applying MAC failed", ex);
            Status = "Error: " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task RestoreAsync()
    {
        if (IsBusy || SelectedAdapter is null) return;
        var adapter = SelectedAdapter;
        IsBusy = true;
        Status = $"Restoring permanent MAC on '{adapter.Description}'...";
        try
        {
            var progress = new Progress<string>(s => App.Current.Dispatcher.Invoke(() => AddLog(s)));
            await _mac.RestorePermanentAsync(adapter.InterfaceGuid, progress);
            await ReloadAdaptersAsync();
            Status = "Permanent MAC restored (override removed).";
            _log.Info("macchanger", $"restored permanent MAC on {adapter.InterfaceGuid}");
        }
        catch (Exception ex)
        {
            _log.Error("macchanger", "restoring permanent MAC failed", ex);
            Status = "Error: " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private void RandomMac()
    {
        MacText = _mac.RandomMac();
        Status = "Random locally-administered MAC generated - type APPLY to confirm.";
    }

    [RelayCommand(CanExecute = nameof(CanSaveProfile))]
    private async Task SaveProfileAsync()
    {
        if (IsBusy || SelectedAdapter is null || !IsMacValid(MacText) || string.IsNullOrWhiteSpace(ProfileName)) return;
        var adapter = SelectedAdapter;
        try
        {
            await _mac.SaveProfileAsync(ProfileName.Trim(), adapter.InterfaceGuid, NormalizeMac(MacText));
            ProfileName = "";
            await ReloadProfilesAsync();
            Status = "MAC profile saved.";
            _log.Info("macchanger", "profile saved");
        }
        catch (Exception ex)
        {
            _log.Error("macchanger", "saving profile failed", ex);
            Status = "Error: " + ex.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseSelectedProfile))]
    private void ApplyProfile()
    {
        if (SelectedProfile is null) return;
        MacText = FormatMac(SelectedProfile.Mac);
        ApplyConfirm = "";
        Status = $"Profile '{SelectedProfile.Name}' loaded - type APPLY to confirm.";
    }

    [RelayCommand(CanExecute = nameof(CanUseSelectedProfile))]
    private async Task DeleteProfileAsync()
    {
        if (IsBusy || SelectedProfile is null) return;
        var profile = SelectedProfile;
        try
        {
            await _mac.DeleteProfileAsync(profile.Id);
            App.Current.Dispatcher.Invoke(() => Profiles.Remove(profile));
            Status = $"Profile '{profile.Name}' deleted.";
            _log.Info("macchanger", $"profile '{profile.Name}' deleted");
        }
        catch (Exception ex)
        {
            _log.Error("macchanger", "deleting profile failed", ex);
            Status = "Error: " + ex.Message;
        }
    }

    // ---- helpers ----

    private async Task ReloadAdaptersAsync()
    {
        var adapters = await _mac.ListAdaptersAsync();
        App.Current.Dispatcher.Invoke(() =>
        {
            var previous = SelectedAdapter?.InterfaceGuid;
            Adapters.Clear();
            foreach (var a in adapters) Adapters.Add(a);
            SelectedAdapter = Adapters.FirstOrDefault(a => a.InterfaceGuid == previous) ?? Adapters.FirstOrDefault();
        });
    }

    private async Task ReloadProfilesAsync()
    {
        var profiles = await _db.GetMacProfilesAsync();
        App.Current.Dispatcher.Invoke(() =>
        {
            Profiles.Clear();
            foreach (var p in profiles) Profiles.Add(p);
        });
    }

    private void AddLog(string line)
    {
        LogLines.Add($"[{DateTime.Now:HH:mm:ss}] {line}");
        while (LogLines.Count > 400) LogLines.RemoveAt(0);
    }
}
