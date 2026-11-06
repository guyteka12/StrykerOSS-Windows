using System.Diagnostics;
using System.Text;

namespace StrykerOSS.Core.Services;

/// <summary>
/// Arsenal: custom scanner/exploit command templates stored in the project DB.
/// Real process execution on this machine (tool-aware for nmap/nuclei).
/// Educational safety guard refuses destructive command strings.
/// </summary>
public sealed class ArsenalService : IArsenalService
{
    // Educational safety guard - these substrings (case-insensitive) get refused.
    private static readonly string[] Forbidden = { "del ", "rm ", "format", "shutdown" };

    private readonly IDbService _db;
    private readonly IToolLocator _tools;
    private readonly ILogger _log;

    public ArsenalService(IDbService db, IToolLocator tools, ILogger log)
    {
        _db = db;
        _tools = tools;
        _log = log;
    }

    // ---------------- CRUD ----------------

    public Task<List<ArsenalTemplate>> ListAsync() => _db.GetTemplatesAsync();

    public async Task<int> SaveAsync(ArsenalTemplate t)
    {
        // SaveTemplateAsync is an upsert: Id == 0 inserts, Id > 0 updates; returns the real row id.
        var id = await _db.SaveTemplateAsync(t);
        _log.Info("arsenal", $"modèle enregistré #{id} '{t.Name}' ({t.Category})");
        return id;
    }

    public Task DeleteAsync(int id)
    {
        _log.Info("arsenal", $"modèle supprimé #{id}");
        return _db.DeleteTemplateAsync(id);
    }

    public async Task SeedIfEmptyAsync()
    {
        var all = await _db.GetTemplatesAsync();
        if (all.Count > 0) return;

        foreach (var t in BuiltInTemplates())
        {
            await _db.SaveTemplateAsync(new ArsenalTemplate(0, t.Name, t.Category, t.Command, t.Description));
        }
        _log.Info("arsenal", $"{BuiltInTemplates().Count} modèles intégrés initialisés");
    }

    private static List<(string Name, string Category, string Command, string Description)> BuiltInTemplates() => new()
    {
        // Noms/catégories/descriptions affichés à l'utilisateur (traduits) - les commandes restent intactes.
        ("Nmap rapide",            "reconnaissance", "nmap -T4 -F {IP}",                                     "Scan TCP rapide des ports courants"),
        ("Détection de services",  "reconnaissance", "nmap -sV {IP}",                                        "Détecte les versions des services"),
        ("Balayage TCP complet",   "reconnaissance", "nmap -sT -p- -T4 {IP}",                                "Les 65535 ports TCP"),
        ("Scan Nuclei",            "web",            "nuclei -u {TARGET_URL}",                               "Scan de vulnérabilités par modèles"),
        ("Table ARP (locale)",     "local",          "arp -a",                                               "Affiche le cache ARP local"),
        ("Test TCP",               "reconnaissance", "powershell -NoProfile -Command \"Test-NetConnection {IP} -Port {PORT}\"", "Test de connectivité d'un seul port"),
    };

    // ---------------- rendering ----------------

    public string Render(ArsenalTemplate t, string ip, string? port, string? mac, string? gw, string? mask)
    {
        var ipVal = string.IsNullOrWhiteSpace(ip) ? "127.0.0.1" : ip.Trim();
        var portVal = int.TryParse(port?.Trim(), out var p) && p > 0 ? p.ToString() : "80";
        var targetUrl = $"http://{ipVal}:{portVal}";

        return t.Command
            .Replace("{IP}", ipVal, StringComparison.Ordinal)
            .Replace("{PORT}", portVal, StringComparison.Ordinal)
            .Replace("{MAC}", Empty(mac), StringComparison.Ordinal)
            .Replace("{GW}", Empty(gw), StringComparison.Ordinal)
            .Replace("{MASK}", Empty(mask), StringComparison.Ordinal)
            .Replace("{TARGET_URL}", targetUrl, StringComparison.Ordinal);
    }

    private static string Empty(string? s) => string.IsNullOrWhiteSpace(s) ? "" : s.Trim();

    // ---------------- execution ----------------

    public async Task<string> ExecuteAsync(ArsenalTemplate t, string ip, string? port, string? mac,
        string? gw, string? mask, IProgress<string>? log = null, CancellationToken ct = default)
    {
        var cmd = Render(t, ip, port, mac, gw, mask).Trim();
        if (cmd.Length == 0) throw new InvalidOperationException("la commande du modèle est vide");

        // educational safety guard
        var lower = cmd.ToLowerInvariant();
        foreach (var bad in Forbidden)
        {
            if (lower.Contains(bad, StringComparison.Ordinal))
            {
                var refusal = $"refusé : la commande contient le jeton interdit '{bad.Trim()}' (garde-fou pédagogique)";
                _log.Warn("arsenal", refusal);
                log?.Report("[refusé] " + refusal);
                return refusal;
            }
        }

        var args = SplitArgs(cmd);
        if (args.Count == 0) throw new InvalidOperationException("la commande du modèle est vide");

        var first = args[0].ToLowerInvariant();
        var exe = args[0];
        if (first is "nmap" or "nuclei")
        {
            var resolved = _tools.ResolveTool(first)
                ?? throw new InvalidOperationException(
                    $"{first} n'est pas installé - installez-le d'abord (pages Nmap Scanner / Web Scanner).");
            exe = resolved;
            log?.Report($"résolu {first} -> {exe}");
        }
        // other first tokens (arp, powershell, ping, ...) run directly - real Windows tools on PATH.

        log?.Report($"$ {cmd}");

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        for (int i = 1; i < args.Count; i++) psi.ArgumentList.Add(args[i]);

        var outBuf = new StringBuilder();
        using var proc = new Process { StartInfo = psi };
        proc.Start();
        var tOut = PumpAsync(proc.StandardOutput, log, outBuf, ct);
        var tErr = PumpAsync(proc.StandardError, log, outBuf, ct);
        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            log?.Report("[annulé]");
            throw;
        }
        try { await Task.WhenAll(tOut, tErr); } catch { /* pump errors already swallowed */ }

        log?.Report($"[code de sortie {proc.ExitCode}]");
        _log.Info("arsenal", $"modèle '{t.Name}' terminé (code de sortie {proc.ExitCode})");
        lock (outBuf) return outBuf.ToString();
    }

    private static async Task PumpAsync(StreamReader reader, IProgress<string>? log, StringBuilder buf, CancellationToken ct)
    {
        try
        {
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (line.Trim().Length == 0) continue;
                lock (buf) buf.AppendLine(line);
                log?.Report(line);
            }
        }
        catch (OperationCanceledException) { }
        catch { /* stream torn down when the process is killed on cancel */ }
    }

    /// <summary>
    /// Splits a command line into tokens, respecting double quotes
    /// (quotes are removed; a quoted segment always becomes one argument).
    /// </summary>
    public static List<string> SplitArgs(string command)
    {
        var res = new List<string>();
        var cur = new StringBuilder();
        bool inQuotes = false;
        foreach (var c in command)
        {
            if (c == '"') { inQuotes = !inQuotes; continue; }
            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (cur.Length > 0) { res.Add(cur.ToString()); cur.Clear(); }
            }
            else cur.Append(c);
        }
        if (cur.Length > 0) res.Add(cur.ToString());
        return res;
    }
}
