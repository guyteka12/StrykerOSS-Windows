using Microsoft.Extensions.DependencyInjection;
using StrykerOSS.Core;
using StrykerOSS.Core.Services;
using StrykerOSS.Views;

namespace StrykerOSS;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = default!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();

        // ---- core infrastructure ----
        services.AddSingleton<ISettings, SettingsService>();
        services.AddSingleton<ILogger, LogService>();
        services.AddSingleton<IAdmin, AdminService>();
        services.AddSingleton<INavigation, Navigation>();
        services.AddSingleton<IToolLocator, ToolLocator>();

        // ---- data & reports ----
        services.AddSingleton<IDbService, DbService>();
        services.AddSingleton<IReportService, ReportService>();

        // ---- modules: services ----
        services.AddSingleton<IOuiService, OuiService>();
        services.AddSingleton<IWifiService, WifiService>();
        services.AddSingleton<IMacChanger, MacChangerService>();
        services.AddSingleton<IHandshakeService, HandshakeService>();
        services.AddSingleton<IHidService, HidService>();
        services.AddSingleton<ILanService, LanService>();
        services.AddSingleton<IPortScanner, PortScannerService>();
        services.AddSingleton<INmapService, NmapService>();
        services.AddSingleton<INucleiService, NucleiService>();
        services.AddSingleton<IExploitDbService, ExploitDbService>();
        services.AddSingleton<IArsenalService, ArsenalService>();
        services.AddSingleton<IBleService, BleService>();
        services.AddSingleton<IGeoMacService, GeoMacService>();
        services.AddSingleton<IMetasploit, MetasploitService>();
        services.AddSingleton<ITerminalService, TerminalService>();
        services.AddSingleton<IUsbService, UsbService>();
        services.AddSingleton<IToolsManager, ToolsManagerService>();

        // ---- view models (singletons: state survives navigation) ----
        services.AddSingleton<ViewModels.MainWindowVm>();
        services.AddSingleton<ViewModels.DashboardVm>();
        services.AddSingleton<ViewModels.WifiVm>();
        services.AddSingleton<ViewModels.LanVm>();
        services.AddSingleton<ViewModels.NmapVm>();
        services.AddSingleton<ViewModels.NucleiVm>();
        services.AddSingleton<ViewModels.ExploitDbVm>();
        services.AddSingleton<ViewModels.ArsenalVm>();
        services.AddSingleton<ViewModels.HandshakesVm>();
        services.AddSingleton<ViewModels.WhisperVm>();
        services.AddSingleton<ViewModels.MacChangerVm>();
        services.AddSingleton<ViewModels.HidVm>();
        services.AddSingleton<ViewModels.UsbVm>();
        services.AddSingleton<ViewModels.MsfVm>();
        services.AddSingleton<ViewModels.SessionsVm>();
        services.AddSingleton<ViewModels.ConsoleVm>();
        services.AddSingleton<ViewModels.GeoMacVm>();
        services.AddSingleton<ViewModels.ProjectsVm>();
        services.AddSingleton<ViewModels.ReportsVm>();
        services.AddSingleton<ViewModels.ToolsVm>();
        services.AddSingleton<ViewModels.LogsVm>();
        services.AddSingleton<ViewModels.SettingsVm>();

        // views transient, resolved with their singleton VM
        services.AddTransient<DashboardView>();
        services.AddTransient<WifiView>();
        services.AddTransient<LanView>();
        services.AddTransient<NmapView>();
        services.AddTransient<NucleiView>();
        services.AddTransient<ExploitDbView>();
        services.AddTransient<ArsenalView>();
        services.AddTransient<HandshakesView>();
        services.AddTransient<WhisperView>();
        services.AddTransient<MacChangerView>();
        services.AddTransient<HidView>();
        services.AddTransient<UsbView>();
        services.AddTransient<MsfView>();
        services.AddTransient<SessionsView>();
        services.AddTransient<ConsoleView>();
        services.AddTransient<GeoMacView>();
        services.AddTransient<ProjectsView>();
        services.AddTransient<ReportsView>();
        services.AddTransient<ToolsView>();
        services.AddTransient<LogsView>();
        services.AddTransient<SettingsView>();

        services.AddSingleton<Func<string, UserControl>>(sp => key => Pages.Create(key, sp));

        Services = services.BuildServiceProvider();

        // consent gate (educational / authorized testing only)
        var settings = Services.GetRequiredService<ISettings>();
        if (!settings.Value.ConsentAccepted)
        {
            var consent = new ConsentWindow();
            consent.ShowDialog();
            if (!consent.Accepted)
            {
                Shutdown();
                return;
            }
            settings.Value.ConsentAccepted = true;
            settings.Value.ConsentDate = DateTime.UtcNow;
            settings.Save();
        }

        var log = Services.GetRequiredService<ILogger>();
        var admin = Services.GetRequiredService<IAdmin>();
        log.Info("core", $"StrykerOSS started - elevated={admin.IsElevated} - {admin.WindowsVersion()}");

        // async init of services that need it (db schema, OUI database)
        _ = Task.Run(async () =>
        {
            try
            {
                await Services.GetRequiredService<IDbService>().InitAsync();
                await Services.GetRequiredService<IOuiService>().InitializeAsync();
                log.Info("core", "Database + OUI vendor database ready");
            }
            catch (Exception ex)
            {
                log.Error("core", "Background init failed", ex);
            }
        });

        var win = new MainWindow(Services);
        MainWindow = win;
        win.Show();
    }

    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        try { Services?.GetService<ILogger>()?.Error("ui", "Unhandled exception", e.Exception); } catch { }
        MessageBox.Show(e.Exception.Message, "StrykerOSS error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
