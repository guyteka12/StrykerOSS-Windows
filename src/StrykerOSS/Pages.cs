using Microsoft.Extensions.DependencyInjection;

namespace StrykerOSS;

/// <summary>
/// Central page registry. Maps navigation keys to real views (each view takes its VM via ctor).
/// </summary>
public static class Pages
{
    public static UserControl Create(string key, IServiceProvider sp)
        => key switch
        {
            "dashboard"  => sp.GetRequiredService<Views.DashboardView>(),
            "wifi"       => sp.GetRequiredService<Views.WifiView>(),
            "lan"        => sp.GetRequiredService<Views.LanView>(),
            "nmap"       => sp.GetRequiredService<Views.NmapView>(),
            "nuclei"     => sp.GetRequiredService<Views.NucleiView>(),
            "exploitdb"  => sp.GetRequiredService<Views.ExploitDbView>(),
            "arsenal"    => sp.GetRequiredService<Views.ArsenalView>(),
            "handshakes" => sp.GetRequiredService<Views.HandshakesView>(),
            "whisper"    => sp.GetRequiredService<Views.WhisperView>(),
            "macchanger" => sp.GetRequiredService<Views.MacChangerView>(),
            "hid"        => sp.GetRequiredService<Views.HidView>(),
            "usb"        => sp.GetRequiredService<Views.UsbView>(),
            "metasploit" => sp.GetRequiredService<Views.MsfView>(),
            "sessions"   => sp.GetRequiredService<Views.SessionsView>(),
            "console"    => sp.GetRequiredService<Views.ConsoleView>(),
            "geomac"     => sp.GetRequiredService<Views.GeoMacView>(),
            "projects"   => sp.GetRequiredService<Views.ProjectsView>(),
            "reports"    => sp.GetRequiredService<Views.ReportsView>(),
            "tools"      => sp.GetRequiredService<Views.ToolsView>(),
            "logs"       => sp.GetRequiredService<Views.LogsView>(),
            "settings"   => sp.GetRequiredService<Views.SettingsView>(),
            _            => sp.GetRequiredService<Views.DashboardView>()
        };
}
