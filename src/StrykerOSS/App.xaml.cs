using Microsoft.Extensions.DependencyInjection;
using StrykerOSS.Core;
using StrykerOSS.Core.Services;
using StrykerOSS.Views;

namespace StrykerOSS;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = default!;

    public App()
    {
        // Filet de sécurité global : AUCUNE exception ne doit fermer l'application
        // silencieusement. Toutes les sources d'exceptions non gérées sont couvertes.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();

        // ---- infrastructure ----
        services.AddSingleton<ISettings, SettingsService>();
        services.AddSingleton<ILogger, LogService>();
        services.AddSingleton<IAdmin, AdminService>();
        services.AddSingleton<INavigation, Navigation>();
        services.AddSingleton<IToolLocator, ToolLocator>();

        // ---- données & rapports ----
        services.AddSingleton<IDbService, DbService>();
        services.AddSingleton<IReportService, ReportService>();

        // ---- modules : services ----
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
        services.AddSingleton<IProvisioning, ProvisioningService>();

        // ---- view models (singletons : l'état survit à la navigation) ----
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

        // vues transitoires, résolues avec leur VM singleton
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

        var log = Services.GetRequiredService<ILogger>();

        // ---- base de données : vérification BLOQUANTE mais résiliente au démarrage ----
        // Le tableau de bord dépend du schéma SQLite : on l'initialise ICI, avec
        // réessais, et on signale clairement en français si quelque chose échoue.
        var dbOk = false;
        Exception? dbError = null;
        for (int attempt = 1; attempt <= 3 && !dbOk; attempt++)
        {
            try
            {
                Services.GetRequiredService<IDbService>().InitAsync().GetAwaiter().GetResult();
                dbOk = true;
            }
            catch (Exception ex)
            {
                dbError = ex;
                log.Error("core", $"Initialisation de la base (tentative {attempt}/3) échouée", ex);
                System.Threading.Thread.Sleep(400);
            }
        }
        if (!dbOk)
        {
            MessageBox.Show(
                "La base de données locale n'a pas pu être initialisée :\n\n" + dbError?.Message +
                "\n\nL'application va quand même démarrer, mais les statistiques du tableau de bord et " +
                "l'enregistrement des résultats peuvent être indisponibles.\n\n" +
                "Journal complet : " + System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "StrykerOSS", "logs.txt"),
                "StrykerOSS - Erreur de base de données",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // ---- portail de consentement (usage éducatif / tests autorisés uniquement) ----
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

        log.Info("core", $"StrykerOSS démarré - admin={Services.GetRequiredService<IAdmin>().IsElevated} - {Services.GetRequiredService<IAdmin>().WindowsVersion()}");

        // ---- initialisation asynchrone des services qui en ont besoin (OUI) ----
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                await Services.GetRequiredService<IOuiService>().InitializeAsync();
                log.Info("core", "Base des fabricants (OUI) prête");
            }
            catch (Exception ex)
            {
                log.Error("core", "Initialisation en arrière-plan échouée", ex);
            }
        });

        var win = new MainWindow(Services);
        MainWindow = win;
        win.Show();

        // ---- assistant de première configuration : installe les outils réels ----
        // (Nmap + Npcap, Nuclei, Hashcat, Metasploit optionnel, index Exploit-DB).
        // Lourd mais 100 % automatique, comme demandé. Rappelable depuis
        // l'onglet Outils -> « Relancer l'assistant d'installation ».
        if (!settings.Value.InitialSetupDone)
        {
            try
            {
                var setup = new SetupWindow(Services);
                setup.ShowDialog();
                settings.Value.InitialSetupDone = true;
                settings.Save();
            }
            catch (Exception ex)
            {
                log.Error("core", "Assistant de première configuration échoué", ex);
                settings.Value.InitialSetupDone = true; // ne pas re-bloquer le démarrage en boucle
                settings.Save();
            }
        }
    }

    // ---------- filets de sécurité globaux ----------

    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        try { Services?.GetService<ILogger>()?.Error("ui", "Exception non gérée sur l'interface", e.Exception); } catch { }
        MessageBox.Show(
            "Une erreur est survenue :\n\n" + e.Exception.Message +
            "\n\nL'application continue de fonctionner. Le détail a été consigné dans le journal (onglet Journaux).",
            "StrykerOSS - Erreur", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true; // ne JAMAIS laisser l'application se fermer
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        try { Services?.GetService<ILogger>()?.Error("core", "Exception non gérée (domaine d'application)", ex); } catch { }

        if (e.IsTerminating)
        {
            try
            {
                MessageBox.Show(
                    "Une erreur fatale inattendue impose la fermeture de l'application :\n\n" + ex?.Message +
                    "\n\nSolutions : relancez l'application en tant qu'administrateur ; si le problème persiste, " +
                    "consultez le journal (onglet Journaux) et ouvrez un ticket sur le dépôt GitHub.",
                    "StrykerOSS - Erreur fatale", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { /* le processus se termine de toute façon */ }
        }
    }

    private void OnUnobservedTaskException(object? sender, System.Threading.Tasks.UnobservedTaskExceptionEventArgs e)
    {
        // Une tâche d'arrière-plan oubliée ne doit JAMAIS tuer le processus.
        try { Services?.GetService<ILogger>()?.Error("core", "Tâche non observée", e.Exception?.GetBaseException()); } catch { }
        e.SetObserved();
    }
}
