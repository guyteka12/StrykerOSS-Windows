using System.Diagnostics;

namespace StrykerOSS.Core.Services;

/// <summary>
/// Gestionnaire d'outils : détection réelle (chemin + sonde de version) et installation
/// réelle via winget (nmap, hashcat, wireshark) ou via l'auto-installeur nuclei / le MSI
/// officiel Metasploit. Metasploit propose aussi un mode d'emploi manuel honnête.
/// </summary>
public sealed class ToolsManagerService : IToolsManager
{
    private readonly IToolLocator _tools;
    private readonly INucleiService _nuclei;
    private readonly ISettings _settings;
    private readonly ILogger _log;

    public ToolsManagerService(IToolLocator tools, INucleiService nuclei, ISettings settings, ILogger log)
    {
        _tools = tools;
        _nuclei = nuclei;
        _settings = settings;
        _log = log;
    }

    private static readonly (string Key, string Name, string Hint, string VersionArg)[] Catalog =
    {
        ("nmap",      "Nmap",                 "winget install Insecure.Nmap — ou https://nmap.org/download",      "--version"),
        ("nuclei",    "Nuclei",               "Téléchargement automatique depuis les releases projectdiscovery",  "-version"),
        ("msfvenom",  "Metasploit Framework", "MSI officiel : https://windows.metasploit.com/metasploitframework-latest.msi", "--version"),
        ("hashcat",   "Hashcat",              "winget install hashcat.hashcat — ou https://hashcat.net/hashcat/", "--version"),
        ("tshark",    "Wireshark (tshark)",   "winget install Wireshark.Wireshark — ou https://www.wireshark.org/", "-v"),
    };

    public async Task<List<ToolStatus>> StatusAsync()
    {
        var list = new List<ToolStatus>();
        foreach (var (key, name, hint, versionArg) in Catalog)
        {
            var path = key == "tshark" ? FindTshark() : _tools.ResolveTool(key);
            var version = path != null ? await GetFirstLineAsync(path, versionArg) : null;
            list.Add(new ToolStatus(key, name, path, Found: path != null, Version: version, InstallHint: hint));
        }
        _log.Info("tools", $"état : {list.Count(t => t.Found)}/{list.Count} outil(s) trouvé(s)");
        return list;
    }

    public async Task<bool> InstallAsync(string key, IProgress<string>? log = null, CancellationToken ct = default)
    {
        var k = (key ?? string.Empty).Trim().ToLowerInvariant();
        _log.Info("tools", $"installation demandée : {k}");

        switch (k)
        {
            case "nmap":
                return await WingetInstallAsync("Insecure.Nmap", log, ct);

            case "nuclei":
                log?.Report("Nuclei : téléchargement de la dernière release depuis projectdiscovery GitHub...");
                var nucleiPath = await _nuclei.EnsureInstalledAsync(log, ct);
                var ok = !string.IsNullOrWhiteSpace(nucleiPath) && File.Exists(nucleiPath);
                log?.Report(ok ? $"Nuclei prêt : {nucleiPath}" : "Échec de l'installation de Nuclei.");
                return ok;

            case "hashcat":
                return await WingetInstallAsync("hashcat.hashcat", log, ct);

            case "tshark":
                return await WingetInstallAsync("Wireshark.Wireshark", log, ct);

            case "msfvenom":
                log?.Report("Metasploit Framework s'installe désormais automatiquement via son MSI officiel.");
                log?.Report("Téléchargement : https://windows.metasploit.com/metasploitframework-latest.msi");
                log?.Report("Astuce : l'assistant « Gestionnaire d'outils -> Relancer l'assistant d'installation » fait tout automatiquement.");
                log?.Report("Après installation, démarrez le démon RPC une fois :  msfrpcd -P <votre-mot-de-passe> -S -p 55553 -a 127.0.0.1");
                return false;

            default:
                log?.Report($"Clé d'outil inconnue « {key} ». Clés connues : nmap, nuclei, hashcat, tshark, msfvenom.");
                return false;
        }
    }

    public string ToolsDir()
    {
        try { return _tools.ToolsDir(); }
        catch { return _settings.Value.ToolsDir; }
    }

    // ---------------------------------------------------------- helpers

    private static string? FindTshark()
    {
        foreach (var p in new[]
                 {
                     @"C:\Program Files\Wireshark\tshark.exe",
                     @"C:\Program Files (x86)\Wireshark\tshark.exe",
                 })
            if (File.Exists(p)) return p;
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try { var p = Path.Combine(dir, "tshark.exe"); if (File.Exists(p)) return p; } catch { }
        }
        return null;
    }

    private async Task<bool> WingetInstallAsync(string packageId, IProgress<string>? log, CancellationToken ct)
    {
        log?.Report($"Exécution : winget install --id {packageId} -e --accept-source-agreements --accept-package-agreements");

        var psi = new ProcessStartInfo
        {
            FileName = "winget.exe",
            Arguments = $"install --id {packageId} -e --silent --accept-source-agreements --accept-package-agreements",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var p = Process.Start(psi);
        if (p == null)
        {
            log?.Report("winget.exe est introuvable — installez d'abord « Programmes d'installation d'applications » depuis le Microsoft Store, ou utilisez l'assistant d'installation (méthodes de secours intégrées).");
            _log.Warn("tools", $"winget absent, impossible d'installer {packageId}");
            return false;
        }

        p.OutputDataReceived += (_, e) => { if (e.Data != null) log?.Report(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) log?.Report(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(10)); // délai dur de 600 s
        try
        {
            await p.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { if (!p.HasExited) p.Kill(true); } catch { }
            var cancelledByUser = ct.IsCancellationRequested;
            log?.Report(cancelledByUser ? "Installation annulée." : "winget a dépassé les 600 s — installation abandonnée.");
            _log.Warn("tools", $"winget {packageId} : {(cancelledByUser ? "annulé" : "délai dépassé")}");
            return false;
        }

        var code = p.ExitCode;
        log?.Report(code == 0
            ? $"winget : « {packageId} » installé avec succès. Redémarrez l'application ou cliquez sur Actualiser pour relancer la détection."
            : $"winget s'est terminé avec le code {code} pour « {packageId} ».");
        _log.Info("tools", $"winget {packageId} -> code {code}");
        return code == 0;
    }

    /// <summary>Exécute « exe versionArg » avec un délai de 5 s et renvoie la première ligne de sortie, s'il y en a une.</summary>
    private static async Task<string?> GetFirstLineAsync(string exe, string versionArg)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = versionArg,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return null;

            var first = await p.StandardOutput.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(first))
                first = await p.StandardError.ReadLineAsync();

            if (!p.WaitForExit(5000))
            {
                try { p.Kill(true); } catch { }
            }
            return string.IsNullOrWhiteSpace(first) ? null : first.Trim();
        }
        catch
        {
            return null; // wrappers batch ou binaires cassés -> version inconnue
        }
    }
}
