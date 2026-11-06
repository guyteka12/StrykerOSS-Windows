using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StrykerOSS.Core;
using StrykerOSS.Core.Services;

namespace StrykerOSS.ViewModels;

public partial class MainWindowVm : ObservableObject
{
    private readonly INavigation _nav;
    private readonly Func<string, UserControl> _pageFactory;
    private readonly IAdmin _admin;

    public ObservableCollection<NavItem> NavItems { get; } = new();

    [ObservableProperty] private string _selectedKey = "dashboard";
    [ObservableProperty] private UserControl? _currentView;
    [ObservableProperty] private string _statusText = "Ready";

    public MainWindowVm(INavigation nav, IAdmin admin, Func<string, UserControl> pageFactory)
    {
        _nav = nav;
        _admin = admin;
        _pageFactory = pageFactory;

        foreach (var n in BuildNav()) NavItems.Add(n);

        _nav.Navigated += key =>
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                CurrentView = _pageFactory(key);
                StatusText = $"{Label(key)}";
            });
        };

        if (!_admin.IsElevated)
            StatusText = "WARNING: not elevated - some modules need Administrator rights";
    }

    private static List<NavItem> BuildNav() => new()
    {
        new("dashboard",   "Dashboard",         "\uE80F"),
        new("wifi",        "Wi-Fi Networks",    "\uE701"),
        new("lan",         "Local Network",     "\uE81C"),
        new("nmap",        "Nmap Scanner",      "\uE721"),
        new("nuclei",      "Web Scanner",       "\uE774"),
        new("exploitdb",   "Exploit Database",  "\uE8B7"),
        new("arsenal",     "Arsenal",           "\uE70F"),
        new("handshakes",  "Handshakes",        "\uE72E"),
        new("whisper",     "WhisperPair (BLE)", "\uE702"),
        new("macchanger",  "MAC Changer",       "\uE8C8"),
        new("hid",         "HID Attacks",       "\uE765"),
        new("usb",         "USB Arsenal",       "\uE88E"),
        new("metasploit",  "Metasploit",        "\uE945"),
        new("sessions",    "Sessions",          "\uE9D9"),
        new("console",     "Console",           "\uE756"),
        new("geomac",      "GeoMac",            "\uE739"),
        new("projects",    "Projects",          "\uE838"),
        new("reports",     "Reports",           "\uE8A5"),
        new("tools",       "Tools Manager",     "\uE90F"),
        new("logs",        "Logs",              "\uE7C3"),
        new("settings",    "Settings",          "\uE713"),
    };

    private static string Label(string key)
    {
        foreach (var n in BuildNav()) if (n.Key == key) return n.Label;
        return key;
    }

    partial void OnSelectedKeyChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value)) _nav.GoTo(value);
    }

    [RelayCommand]
    private void OpenDashboard() => _nav.GoTo("dashboard");
}
