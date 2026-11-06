using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using StrykerOSS.Core.Services;

namespace StrykerOSS.Core.Services;

/// <summary>
/// Real nmap.exe wrapper: runs the actual nmap binary with -oX XML output,
/// streams live stdout/stderr to the log, parses the real XML result and
/// exports a dark-styled HTML report.
/// </summary>
public sealed class NmapService : INmapService
{
    private static readonly List<NmapProfile> ProfileList = new()
    {
        new("default",  "Scan par défaut",  "-sV -T3",       "Détection des services et des versions, temporisation normale - premier aperçu solide d'un hôte."),
        new("quick",    "Scan rapide",      "-T4 -F",        "Analyse rapide des 100 ports les plus courants."),
        new("full-tcp", "Scan TCP complet", "-sT -p- -T4",   "Les 65535 ports TCP - lent mais complet."),
        new("intense",  "Scan intense",     "-A -T4 -sV -O", "Agressif : détection d'OS, versions, scripts par défaut, traceroute."),
        new("no-ping",  "Scan sans ping",   "-Pn -sV -T4",   "Ignore la découverte d'hôtes - scanne même si l'ICMP est bloqué."),
    };

    private readonly IToolLocator _tools;
    private readonly ISettings _settings;

    public NmapService(IToolLocator tools, ISettings settings)
    {
        _tools = tools;
        _settings = settings;
    }

    public IReadOnlyList<NmapProfile> Profiles => ProfileList;

    public string? FindNmap() => _tools.ResolveTool("nmap");

    public async Task<NmapRun> RunAsync(string target, string args, IProgress<string>? log = null,
        CancellationToken ct = default, string? profileName = null)
    {
        var nmap = FindNmap()
            ?? throw new InvalidOperationException("Nmap introuvable. Installez-le depuis https://nmap.org ou via le gestionnaire d'outils.");

        if (string.IsNullOrWhiteSpace(target))
            throw new InvalidOperationException("La cible est vide - saisissez une IP, une plage, un CIDR ou un nom d'hôte.");
        if (string.IsNullOrWhiteSpace(args))
            args = "-sV -T3";

        var started = DateTime.UtcNow;
        var sw = Stopwatch.StartNew();

        var scanDir = Path.Combine(_settings.Value.DataDir, "scans");
        Directory.CreateDirectory(scanDir);
        var xmlPath = Path.Combine(scanDir, $"nmap_{DateTime.Now:yyyyMMdd_HHmmss}.xml");
        var fullArgs = $"{args} -oX \"{xmlPath}\" {target.Trim()}";

        log?.Report(string.IsNullOrWhiteSpace(profileName)
            ? $"nmap {fullArgs}"
            : $"[{profileName}] nmap {fullArgs}");

        var psi = new ProcessStartInfo(nmap, fullArgs)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log?.Report(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) log?.Report("[nmap-err] " + e.Data); };

        if (!process.Start())
            throw new InvalidOperationException("Impossible de démarrer le processus nmap.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            log?.Report("nmap annulé - arborescence de processus interrompue.");
            throw;
        }
        sw.Stop();

        if (!File.Exists(xmlPath))
        {
            var exit = process.HasExited ? process.ExitCode : -1;
            throw new InvalidOperationException($"nmap s'est terminé avec le code {exit} sans produire de sortie XML. La cible est-elle joignable et les options sont-elles valides ?");
        }

        var hosts = ParseHosts(xmlPath);
        log?.Report($"terminé : {hosts.Count} hôte(s) analysé(s), {hosts.Count(h => h.Up)} actif(s), en {sw.Elapsed.TotalSeconds:0.0} s");

        return new NmapRun(target.Trim(), started, sw.Elapsed, hosts, xmlPath);
    }

    public async Task<string> ExportHtmlAsync(NmapRun run, string outDir)
    {
        if (run == null) throw new ArgumentNullException(nameof(run));
        Directory.CreateDirectory(outDir);
        var path = Path.Combine(outDir, $"nmap_report_{DateTime.Now:yyyyMMdd_HHmmss}.html");

        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>StrykerOSS - Rapport Nmap</title>");
        sb.Append("<style>body{background:#0B0B12;color:#E8E8F2;font-family:'Segoe UI',Arial,sans-serif;margin:24px}");
        sb.Append("h1{color:#A78BFA}h2{color:#E8E8F2;border-bottom:1px solid #2A2A3C;padding-bottom:6px;font-size:17px}");
        sb.Append("table{border-collapse:collapse;width:100%;margin:8px 0 18px 0}th,td{border:1px solid #2A2A3C;padding:6px 10px;font-size:13px;text-align:left}");
        sb.Append("th{background:#1B1B29;color:#A78BFA}tr:nth-child(even) td{background:#14141F}");
        sb.Append(".muted{color:#6E6E88;font-size:12px}.open{color:#22C55E;font-weight:bold}.filtered{color:#F59E0B;font-weight:bold}.closed{color:#6E6E88}");
        sb.Append(".card{background:#14141F;border:1px solid #2A2A3C;border-radius:10px;padding:14px;margin:0 0 16px 0}</style></head><body>");
        sb.AppendLine();
        sb.AppendLine("<h1>StrykerOSS - Rapport Nmap</h1>");
        sb.AppendLine($"<div class=\"card\"><b>Cible :</b> {Esc(run.Target)}<br/>" +
                      $"<b>Démarré le :</b> {run.Started:yyyy-MM-dd HH:mm:ss} UTC<br/>" +
                      $"<b>Durée :</b> {run.Duration.TotalSeconds:0.0} s<br/>" +
                      $"<b>Hôtes :</b> {run.Hosts.Count} scanné(s), {run.Hosts.Count(h => h.Up)} actif(s)<br/>" +
                      $"<span class=\"muted\">XML brut : {Esc(run.RawXmlPath ?? "-")}</span></div>");

        foreach (var h in run.Hosts)
        {
            var label = Esc(h.Ip) + (string.IsNullOrWhiteSpace(h.Hostname) ? "" : " - " + Esc(h.Hostname));
            sb.AppendLine($"<h2>{label} <span class=\"muted\">[{(h.Up ? "actif" : "hors ligne")}]</span></h2>");

            var meta = new List<string>();
            if (!string.IsNullOrWhiteSpace(h.Mac))
                meta.Add("MAC " + Esc(h.Mac) + (string.IsNullOrWhiteSpace(h.MacVendor) ? "" : $" ({Esc(h.MacVendor)})"));
            if (!string.IsNullOrWhiteSpace(h.OsGuess)) meta.Add("OS supposé : " + Esc(h.OsGuess));
            if (meta.Count > 0) sb.AppendLine("<div class=\"muted\">" + string.Join(" | ", meta) + "</div>");

            if (h.Ports.Count == 0) continue;
            sb.AppendLine("<table><tr><th>Port</th><th>Proto</th><th>État</th><th>Service</th><th>Version</th><th>Script</th></tr>");
            foreach (var p in h.Ports)
            {
                sb.AppendLine($"<tr><td>{p.Port}</td><td>{Esc(p.Proto)}</td><td class=\"{Esc(p.State)}\">{Esc(p.State)}</td>" +
                              $"<td>{Esc(p.Service)}</td><td>{Esc(p.Version ?? "")}</td><td class=\"muted\">{Esc(p.ScriptOutput ?? "")}</td></tr>");
            }
            sb.AppendLine("</table>");
        }

        sb.AppendLine("<p class=\"muted\">Généré par StrykerOSS (GPL-3.0) - usage éducatif uniquement. Ne scannez que des systèmes que vous possédez ou êtes autorisé à tester.</p>");
        sb.AppendLine("</body></html>");

        await File.WriteAllTextAsync(path, sb.ToString()).ConfigureAwait(false);
        return path;
    }

    // ---------------- internals ----------------

    private static List<NmapHost> ParseHosts(string xmlPath)
    {
        var hosts = new List<NmapHost>();
        var doc = XDocument.Load(xmlPath);
        if (doc.Root == null) return hosts;

        // nmap XML may or may not carry a default namespace - match on LocalName to handle both.
        foreach (var h in doc.Root.Descendants().Where(e => e.Name.LocalName == "host"))
        {
            var up = h.Elements().Any(e => e.Name.LocalName == "status" &&
                                           (string?)e.Attribute("state") == "up");

            string? ip = null, mac = null, macVendor = null;
            foreach (var a in h.Elements().Where(e => e.Name.LocalName == "address"))
            {
                var type = (string?)a.Attribute("addrtype") ?? "";
                if (type == "ipv4" && ip == null) ip = (string?)a.Attribute("addr");
                else if (type == "mac" && mac == null)
                {
                    mac = (string?)a.Attribute("addr");
                    macVendor = (string?)a.Attribute("vendor");
                }
            }

            var hostname = h.Elements()
                .Where(e => e.Name.LocalName == "hostnames").Elements()
                .Where(e => e.Name.LocalName == "hostname")
                .Select(e => (string?)e.Attribute("name"))
                .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

            var osGuess = h.Elements()
                .Where(e => e.Name.LocalName == "os").Elements()
                .Where(e => e.Name.LocalName == "osmatch")
                .Select(e => (string?)e.Attribute("name"))
                .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

            var ports = new List<NmapPort>();
            foreach (var pe in h.Elements()
                         .Where(e => e.Name.LocalName == "ports").Elements()
                         .Where(e => e.Name.LocalName == "port"))
            {
                var portId = (int?)pe.Attribute("portid") ?? 0;
                var proto = (string?)pe.Attribute("protocol") ?? "tcp";

                var st = pe.Elements().FirstOrDefault(e => e.Name.LocalName == "state");
                var state = (string?)st?.Attribute("state") ?? "unknown";

                var svc = pe.Elements().FirstOrDefault(e => e.Name.LocalName == "service");
                var svcName = (string?)svc?.Attribute("name") ?? "unknown";
                var version = (string?)svc?.Attribute("version");

                var script = pe.Elements()
                    .Where(e => e.Name.LocalName == "script")
                    .Select(e => (string?)e.Attribute("output"))
                    .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

                ports.Add(new NmapPort(portId, proto, state, svcName, version, script));
            }

            hosts.Add(new NmapHost(ip ?? "?", hostname, osGuess, mac, macVendor, up, ports));
        }
        return hosts;
    }

    private static string Esc(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }
}
