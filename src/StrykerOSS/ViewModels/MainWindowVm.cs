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
    [ObservableProperty] private string _statusText = "Prêt";

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
            StatusText = "ATTENTION : sans élévation — certains modules nécessitent les droits administrateur";
    }

    private static List<NavItem> BuildNav() => new()
    {
        new("dashboard",   "Tableau de bord",        "\uE80F"),
        new("wifi",        "Réseaux Wi-Fi",          "\uE701"),
        new("lan",         "Réseau local",           "\uE81C"),
        new("nmap",        "Scanner Nmap",           "\uE721"),
        new("nuclei",      "Scanner web",            "\uE774"),
        new("exploitdb",   "Base d'exploits",        "\uE8B7"),
        new("arsenal",     "Arsenal",                "\uE70F"),
        new("handshakes",  "Handshakes",             "\uE72E"),
        new("whisper",     "WhisperPair (BLE)",      "\uE702"),
        new("macchanger",  "Changeur d'adresse MAC", "\uE8C8"),
        new("hid",         "Attaques HID",           "\uE765"),
        new("usb",         "Arsenal USB",            "\uE88E"),
        new("metasploit",  "Metasploit",             "\uE945"),
        new("sessions",    "Sessions",               "\uE9D9"),
        new("console",     "Console",                "\uE756"),
        new("geomac",      "GeoMac",                 "\uE739"),
        new("projects",    "Projets",                "\uE838"),
        new("reports",     "Rapports",               "\uE8A5"),
        new("tools",       "Gestionnaire d'outils",  "\uE90F"),
        new("logs",        "Journaux",               "\uE7C3"),
        new("settings",    "Paramètres",             "\uE713"),
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
