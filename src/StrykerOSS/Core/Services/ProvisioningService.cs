using System.Diagnostics;
using System.Net.Http;

namespace StrykerOSS.Core.Services;

/// <summary>
/// Assistant de première configuration : installe RÉELLEMENT chaque outil dont la
/// plateforme a besoin (aucune simulation). Stratégie par composant :
///   - Nmap (+ Npcap)     : winget -> sinon installeur officiel nmap.org en mode silencieux
///   - Nuclei             : archive officielle projectdiscovery (GitHub), déjà gérée par NucleiService
///   - Hashcat            : winget -> sinon Chocolatey si présent -> sinon mode d'emploi
///   - Wireshark (tshark) : winget -> sinon installeur officiel wireshark.org silencieux (optionnel)
///   - Metasploit         : MSI officiel Rapid7 en /qn via msiexec (optionnel, très lourd)
///   - Index Exploit-DB   : CSV officiel (GitLab) pré-téléchargé pour la recherche hors ligne
/// Tout est journalisé en direct dans la fenêtre de l'assistant.
/// </summary>
public sealed class ProvisioningService : IProvisioning
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

    static ProvisioningService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("StrykerOSS/1.0 (educational pentest suite; Windows)");
    }

    private readonly IToolLocator _tools;
    private readonly INucleiService _nuclei;
    private readonly IExploitDbService _exploitdb;
    private readonly ISettings _settings;
    private readonly ILogger _log;

    public ProvisioningService(IToolLocator tools, INucleiService nuclei, IExploitDbService exploitdb,
        ISettings settings, ILogger log)
    {
        _tools = tools;
        _nuclei = nuclei;
        _exploitdb = exploitdb;
        _settings = settings;
        _log = log;
    }

    // ------------------------------------------------- catalogue

    private static readonly (string Key, string Name, string Description, string Size, bool Optional)[] Catalog =
    {
        ("nmap", "Nmap + Npcap",
            "Scanner de réseaux. L'installeur officiel ajoute aussi le pilote de capture Npcap.",
            "≈ 40 Mo", false),
        ("nuclei", "Nuclei (scanner web)",
            "Scanner de vulnérabilités web de Project Discovery + modèles de détection.",
            "≈ 60 Mo", false),
        ("hashcat", "Hashcat",
            "Outil de craquage de mots de passe (analyse des saisies WPA exportées).",
            "≈ 100 Mo", true),
        ("wireshark", "Wireshark / tshark",
            "Analyse des captures réseau (.pcap) en ligne de commande.",
            "≈ 90 Mo", true),
        ("metasploit", "Metasploit Framework",
            "Framework d'exploitation + génération de charges utiles (msfvenom). Installation longue.",
            "≈ 1 Go", true),
        ("exploitdb", "Index Exploit-DB hors ligne",
            "Base locale de vulnérabilités (recherche instantanée sans Internet ensuite).",
            "≈ 20 Mo", false),
    };

    // ------------------------------------------------- état

    public async Task<List<ProvisionItem>> CheckAsync()
    {
        var list = new List<ProvisionItem>();
        foreach (var (key, name, desc, size, optional) in Catalog)
        {
            bool installed = key switch
            {
                "nmap" => _tools.ResolveTool("nmap") != null,
                "nuclei" => _tools.ResolveTool("nuclei") != null,
                "hashcat" => _tools.ResolveTool("hashcat") != null,
                "wireshark" => FindTshark() != null,
                "metasploit" => _tools.ResolveTool("msfconsole") != null,
                "exploitdb" => _exploitdb.Total > 0,
                _ => false,
            };
            list.Add(new ProvisionItem(key, name, desc, size, optional, installed, installed ? "Déjà installé" : "Non détecté"));
        }
        return await Task.FromResult(list);
    }

    // ------------------------------------------------- installation

    public async Task<bool> InstallAsync(string key, IProgress<string>? log = null, CancellationToken ct = default)
    {
        var k = (key ?? "").Trim().ToLowerInvariant();
        _log.Info("provision", $"installation demandée : {k}");
        log?.Report($"▶ Préparation de l'installation : {k}...");

        bool ok = k switch
        {
            "nmap" => await InstallNmapAsync(log, ct),
            "nuclei" => await InstallNucleiAsync(log, ct),
            "hashcat" => await InstallHashcatAsync(log, ct),
            "wireshark" => await InstallWiresharkAsync(log, ct),
            "metasploit" => await InstallMetasploitAsync(log, ct),
            "exploitdb" => await InstallExploitDbAsync(log, ct),
            _ => false,
        };

        log?.Report(ok ? $"✔ {k} : installation terminée avec succès."
                       : $"✘ {k} : l'installation n'a pas abouti (voir les messages ci-dessus).");
        return ok;
    }

    // ---------------- Nmap ----------------

    private async Task<bool> InstallNmapAsync(IProgress<string>? log, CancellationToken ct)
    {
        if (await TryWingetAsync("Insecure.Nmap", "Nmap", log, ct)) return true;

        log?.Report("winget indisponible ou en échec -> installeur officiel nmap.org (silencieux)...");
        const string url = "https://nmap.org/dist/nmap-7.95-setup.exe";
        var exe = await DownloadToTempAsync(url, "nmap-setup.exe", log, ct);
        if (exe == null) return false;

        log?.Report("Exécution silencieuse de l'installeur Nmap (/S) - le pilote Npcap est inclus...");
        bool ok = await RunInstallerAsync(exe, "/S", log, ct, timeoutMinutes: 12);
        bool found = _tools.ResolveTool("nmap") != null;
        log?.Report(found ? $"Nmap détecté : {_tools.ResolveTool("nmap")}" :
                            "Nmap introuvable après installation - redémarrez l'application puis relisez l'onglet Outils.");
        return ok && found;
    }

    // ---------------- Nuclei ----------------

    private async Task<bool> InstallNucleiAsync(IProgress<string>? log, CancellationToken ct)
    {
        var path = await _nuclei.EnsureInstalledAsync(log, ct);
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
    }

    // ---------------- Hashcat ----------------

    private async Task<bool> InstallHashcatAsync(IProgress<string>? log, CancellationToken ct)
    {
        if (await TryWingetAsync("hashcat.hashcat", "Hashcat", log, ct)) return true;

        if (await TryChocolateyAsync("hashcat", log, ct)) return true;

        log?.Report("Impossible d'installer Hashcat automatiquement (ni winget ni Chocolatey).");
        log?.Report("Mode manuel : téléchargez https://hashcat.net/hashcat/ puis décompressez hashcat.exe");
        log?.Report($"dans le dossier outils : {_tools.ToolsDir()}");
        return false;
    }

    // ---------------- Wireshark ----------------

    private async Task<bool> InstallWiresharkAsync(IProgress<string>? log, CancellationToken ct)
    {
        if (await TryWingetAsync("Wireshark.Wireshark", "Wireshark", log, ct)) return true;

        log?.Report("winget indisponible -> installeur officiel wireshark.org (silencieux)...");
        const string url = "https://1.as.dl.wireshark.org/win64/Wireshark-latest-x64.exe";
        var exe = await DownloadToTempAsync(url, "wireshark-setup.exe", log, ct);
        if (exe == null) return false;

        log?.Report("Installation silencieuse de Wireshark (/S)...");
        bool ok = await RunInstallerAsync(exe, "/S", log, ct, timeoutMinutes: 12);
        return ok && FindTshark() != null;
    }

    // ---------------- Metasploit ----------------

    private async Task<bool> InstallMetasploitAsync(IProgress<string>? log, CancellationToken ct)
    {
        log?.Report("Metasploit Framework : téléchargement du MSI officiel Rapid7 (environ 1 Go, soyez patient)...");
        const string url = "https://windows.metasploit.com/metasploitframework-latest.msi";
        var msi = await DownloadToTempAsync(url, "metasploitframework-latest.msi", log, ct);
        if (msi == null)
        {
            log?.Report("Téléchargement direct impossible -> tentative via winget...");
            return await TryWingetAsync("Metasploit.Framework", "Metasploit", log, ct);
        }

        log?.Report("Installation silencieuse du MSI (msiexec /qn) - peut prendre plusieurs minutes...");
        bool ok = await RunInstallerAsync("msiexec.exe", $"/i \"{msi}\" /qn /norestart", log, ct, timeoutMinutes: 25);
        try { File.Delete(msi); } catch { }
        bool found = _tools.ResolveTool("msfconsole") != null;
        if (ok && !found)
            log?.Report("MSI terminé mais msfconsole introuvable dans le PATH - redémarrez l'application pour rafraîchir la détection.");
        return ok;
    }

    // ---------------- Exploit-DB ----------------

    private async Task<bool> InstallExploitDbAsync(IProgress<string>? log, CancellationToken ct)
    {
        await _exploitdb.EnsureDbAsync(log, ct);
        return _exploitdb.Total > 0;
    }

    // ------------------------------------------------- helpers

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

    /// <summary>Tente une installation winget silencieuse ; renvoie true uniquement si l'outil est réellement détecté ensuite.</summary>
    private async Task<bool> TryWingetAsync(string packageId, string friendlyName, IProgress<string>? log, CancellationToken ct)
    {
        log?.Report($"[{friendlyName}] tentative via winget (paquet {packageId})...");
        var winget = FindWinget();
        if (winget == null)
        {
            log?.Report("winget est introuvable sur cette machine (fonctionnalité « App Installer » absente).");
            return false;
        }

        int code = await RunProcessCaptureAsync(winget.Value.path,
            $"install --id {packageId} -e --silent --accept-source-agreements --accept-package-agreements --disable-interactivity",
            log, ct, timeoutMinutes: 15);
        log?.Report(code == 0
            ? $"winget : installation de {packageId} terminée (code 0)."
            : $"winget : code de sortie {code} pour {packageId}.");
        return code == 0;
    }

    private static (string path, string display)? FindWinget()
    {
        string exe = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "winget.exe");
        if (File.Exists(exe)) return (exe, "winget (utilisateur)");
        if (File.Exists(@"C:\Program Files\WindowsApps\winget.exe")) return (@"C:\Program Files\WindowsApps\winget.exe", "winget (système)");
        return null;
    }

    private async Task<bool> TryChocolateyAsync(string packageId, IProgress<string>? log, CancellationToken ct)
    {
        const string choco = @"C:\ProgramData\chocolatey\bin\choco.exe";
        if (!File.Exists(choco)) return false;
        log?.Report($"Tentative via Chocolatey ({packageId})...");
        int code = await RunProcessCaptureAsync(choco, $"install {packageId} -y --no-progress", log, ct, timeoutMinutes: 15);
        return code == 0;
    }

    private async Task<string?> DownloadToTempAsync(string url, string fileName, IProgress<string>? log, CancellationToken ct)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "StrykerOSS-setup");
            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir, fileName);

            log?.Report($"Téléchargement : {url}");
            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            long? total = resp.Content.Headers.ContentLength;

            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            var buffer = new byte[1 << 16];
            long done = 0; int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0)
                    log?.Report($"  téléchargé {done / (1024 * 1024)} Mo / {total / (1024 * 1024)} Mo");
            }
            log?.Report($"Téléchargement terminé : {dest}");
            return dest;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log?.Report($"Échec du téléchargement : {ex.Message}");
            return null;
        }
    }

    /// <summary>Lance un installeur externe (visible dans la liste des process, fenêtres cachées) et attend la fin.</summary>
    private async Task<bool> RunInstallerAsync(string fileName, string args, IProgress<string>? log, CancellationToken ct, int timeoutMinutes)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = args,
                UseShellExecute = true,          // les installeurs exigeant l'élévation passent par UAC
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) { log?.Report($"Impossible de démarrer : {fileName}"); return false; }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(timeoutMinutes));
            try { await p.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { if (!p.HasExited) p.Kill(true); } catch { }
                log?.Report(ct.IsCancellationRequested ? "Installation annulée par l'utilisateur." : $"Délai dépassé ({timeoutMinutes} min).");
                return false;
            }
            log?.Report($"{Path.GetFileName(fileName)} terminé (code {p.ExitCode}).");
            return p.ExitCode == 0 || p.ExitCode == 3010; // 3010 = redémarrage requis, installation réussie
        }
        catch (Exception ex)
        {
            log?.Report($"Erreur pendant l'exécution de {fileName} : {ex.Message}");
            return false;
        }
    }

    private static async Task<int> RunProcessCaptureAsync(string exe, string args, IProgress<string>? log, CancellationToken ct, int timeoutMinutes)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return -1;
            p.OutputDataReceived += (_, e) => { if (e.Data != null) log?.Report("  " + e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) log?.Report("  " + e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(timeoutMinutes));
            try { await p.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                try { if (!p.HasExited) p.Kill(true); } catch { }
                return -2;
            }
            return p.ExitCode;
        }
        catch { return -1; }
    }
}
