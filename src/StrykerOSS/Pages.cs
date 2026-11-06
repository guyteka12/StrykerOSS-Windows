using Microsoft.Extensions.DependencyInjection;

namespace StrykerOSS;

/// <summary>
/// Registre central des pages. Mappe les clés de navigation vers les vues réelles.
/// Sûr par conception : si une vue échoue à se construire, une page d'erreur en
/// français s'affiche au lieu de laisser l'application se fermer.
/// </summary>
public static class Pages
{
    public static UserControl Create(string key, IServiceProvider sp)
    {
        try
        {
            return key switch
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
        catch (Exception ex)
        {
            try { (sp.GetService<Core.ILogger>())?.Error("nav", $"ouverture du module « {key} » échouée", ex); } catch { }
            return BuildErrorPage(key, ex);
        }
    }

    private static UserControl BuildErrorPage(string key, Exception ex)
        => new()
        {
            Content = new System.Windows.Controls.Grid
            {
                Background = (System.Windows.Media.Brush)Application.Current.Resources["BgBrush"],
                Margin = new Thickness(18),
                Children =
                {
                    new System.Windows.Controls.StackPanel
                    {
                        VerticalAlignment = VerticalAlignment.Center,
                        Children =
                        {
                            new System.Windows.Controls.TextBlock
                            {
                                Text = "Impossible d'ouvrir ce module",
                                FontSize = 26, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap,
                                Foreground = (System.Windows.Media.Brush)Application.Current.Resources["TextBrush"],
                            },
                            new System.Windows.Controls.TextBlock
                            {
                                Text = $"Module concerné : « {key} ».\n\nDétail technique : {ex.Message}\n\n" +
                                       "Essayez de relancer l'application en tant qu'administrateur. " +
                                       "Le détail complet est disponible dans l'onglet Journaux.",
                                Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap,
                                Foreground = (System.Windows.Media.Brush)Application.Current.Resources["DimBrush"],
                            },
                        },
                    },
                },
            },
        };
}
