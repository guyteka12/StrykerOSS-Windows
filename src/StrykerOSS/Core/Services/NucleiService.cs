using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace StrykerOSS.Core.Services;

/// <summary>
/// Real nuclei (projectdiscovery) integration for Windows.
/// - Finds an existing nuclei.exe (settings / tools dir / PATH).
/// - Installs the OFFICIAL pinned windows-x64 release zip from GitHub.
/// - Runs real scans (-list/-j/-o) and parses the real JSONL findings file.
/// </summary>
public sealed class NucleiService : INucleiService
{
    // Pinned official release (do not bump casually - reproducibility matters).
    private const string NucleiVersion = "3.3.9";
    private const string NucleiZipUrl =
        "https://github.com/projectdiscovery/nuclei/releases/download/v3.3.9/nuclei_3.3.9_windows_x64.zip";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(15) };

    static NucleiService()
    {
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("StrykerOSS/1.0 (educational pentest suite; Windows)");
    }

    private readonly IToolLocator _tools;
    private readonly ISettings _settings;
    private readonly ILogger _log;

    public NucleiService(IToolLocator tools, ISettings settings, ILogger log)
    {
        _tools = tools;
        _settings = settings;
        _log = log;
    }

    public string? FindNuclei() => _tools.ResolveTool("nuclei");

    // ---------------- install ----------------

    public async Task<string> EnsureInstalledAsync(IProgress<string>? log = null, CancellationToken ct = default)
    {
        try
        {
            var found = FindNuclei();
            if (found != null)
            {
                log?.Report($"nuclei déjà présent : {found}");
                return found;
            }

            var toolsDir = _tools.ToolsDir();
            var destDir = Path.Combine(toolsDir, "nuclei");
            var destExe = Path.Combine(destDir, "nuclei.exe");
            if (File.Exists(destExe))
            {
                RegisterPath(destExe);
                log?.Report($"nuclei déjà présent : {destExe}");
                return destExe;
            }

            Directory.CreateDirectory(toolsDir);
            var zipPath = Path.Combine(toolsDir, $"nuclei_{NucleiVersion}_windows_x64.zip.part");
            try
            {
                log?.Report($"téléchargement de nuclei v{NucleiVersion} (windows x64, version épinglée) depuis projectdiscovery...");
                await DownloadAsync(NucleiZipUrl, zipPath, log, ct);

                log?.Report("extraction de nuclei.exe ...");
                Directory.CreateDirectory(destDir);
                using (var zip = ZipFile.OpenRead(zipPath))
                {
                    var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals("nuclei.exe", StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException("entrée nuclei.exe introuvable dans l'archive zip de la version officielle");
                    entry.ExtractToFile(destExe, overwrite: true);
                }

                RegisterPath(destExe);
                log?.Report($"installé : {destExe}");
                _log.Info("nuclei", $"nuclei v{NucleiVersion} installé dans {destExe}");
                return destExe;
            }
            finally
            {
                try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { /* temp cleanup */ }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.Error("nuclei", "échec de l'installation", ex);
            throw new InvalidOperationException($"échec de l'installation de nuclei : {ex.Message}", ex);
        }
    }

    private void RegisterPath(string exe)
    {
        // ToolLocator checks settings.NucleiPath first - make the new binary discoverable app-wide.
        if (!string.Equals(_settings.Value.NucleiPath, exe, StringComparison.OrdinalIgnoreCase))
        {
            _settings.Value.NucleiPath = exe;
            _settings.Save();
        }
    }

    private static async Task DownloadAsync(string url, string destFile, IProgress<string>? log, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength;

        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = new FileStream(destFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        var buffer = new byte[1 << 16];
        long done = 0, nextReport = 0;
        int read;
        while ((read = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            if (done >= nextReport)
            {
                nextReport += 1 << 20; // report roughly every 1 MB
                log?.Report(total.HasValue
                    ? $"  {done / 1048576.0:0.0} / {total.Value / 1048576.0:0.0} Mo"
                    : $"  {done / 1048576.0:0.0} Mo");
            }
        }
        log?.Report($"téléchargé {done / 1048576.0:0.0} Mo");
    }

    // ---------------- scan ----------------

    public async Task<NucleiRun> ScanAsync(IEnumerable<string> targets, IEnumerable<string>? severities,
        IEnumerable<string>? tags, IProgress<string>? log = null, CancellationToken ct = default)
    {
        var list = (targets ?? Array.Empty<string>()).Select(t => t.Trim())
            .Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count == 0)
            throw new ArgumentException("aucune cible fournie - saisissez au moins un hôte ou une URL", nameof(targets));

        var exe = FindNuclei()
            ?? throw new InvalidOperationException(
                "nuclei n'est pas installé. Installez-le depuis la page Scanner Web (télécharge la version officielle épinglée de projectdiscovery pour Windows).");

        var scansDir = Path.Combine(_settings.EnsureDataDir(), "scans");
        Directory.CreateDirectory(scansDir);
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var targetsFile = Path.Combine(scansDir, $"nuclei_targets_{stamp}.txt");
        var outFile = Path.Combine(scansDir, $"nuclei_results_{stamp}.jsonl");

        var lines = list.Select(t => t.Contains("://", StringComparison.Ordinal) ? t : "http://" + t).ToList();
        await File.WriteAllLinesAsync(targetsFile, lines, ct);

        var sev = (severities ?? Array.Empty<string>()).Select(s => s.Trim().ToLowerInvariant())
            .Where(s => s.Length > 0).Distinct().ToList();
        var tagList = (tags ?? Array.Empty<string>()).Select(t => t.Trim())
            .Where(t => t.Length > 0).Distinct().ToList();

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = scansDir
        };
        psi.ArgumentList.Add("-list");
        psi.ArgumentList.Add(targetsFile);
        psi.ArgumentList.Add("-j");
        psi.ArgumentList.Add(outFile);
        psi.ArgumentList.Add("-silent");
        psi.ArgumentList.Add("-nc");
        if (sev.Count > 0) { psi.ArgumentList.Add("-severity"); psi.ArgumentList.Add(string.Join(',', sev)); }
        if (tagList.Count > 0) { psi.ArgumentList.Add("-tags"); psi.ArgumentList.Add(string.Join(',', tagList)); }

        var display = string.Join(' ', psi.ArgumentList.Select(a => a.Contains(' ') ? "\"" + a + "\"" : a));
        log?.Report($"$ \"{exe}\" {display}");
        log?.Report("note : au premier lancement, nuclei télécharge sa base de modèles dans %USERPROFILE%\\nuclei-templates (une seule fois, peut prendre une minute)");

        _log.Info("nuclei", $"scan démarré : cibles=[{string.Join(',', lines)}] gravité=[{string.Join(',', sev)}] tags=[{string.Join(',', tagList)}]");

        var started = DateTime.Now;
        var sw = Stopwatch.StartNew();

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        var pumpOut = PumpAsync(proc.StandardOutput, log, ct);
        var pumpErr = PumpAsync(proc.StandardError, log, ct);
        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
            log?.Report("[annulé] nuclei interrompu");
            throw;
        }
        try { await Task.WhenAll(pumpOut, pumpErr); } catch { /* pump errors already swallowed */ }

        sw.Stop();
        log?.Report($"nuclei s'est terminé avec le code {proc.ExitCode} en {sw.Elapsed.TotalSeconds:0.0} s");

        var findings = ParseResults(outFile, log);
        findings.Sort((a, b) => SeverityRank(b.Severity).CompareTo(SeverityRank(a.Severity)));

        var run = new NucleiRun(string.Join(',', lines), started, sw.Elapsed, findings,
            File.Exists(outFile) ? outFile : null);
        _log.Info("nuclei", $"scan terminé : {findings.Count} constats en {sw.Elapsed.TotalSeconds:0.0} s -> {outFile}");
        return run;
    }

    private static async Task PumpAsync(StreamReader reader, IProgress<string>? log, CancellationToken ct)
    {
        try
        {
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (line.Trim().Length > 0) log?.Report(line);
            }
        }
        catch (OperationCanceledException) { }
        catch { /* stream torn down when the process is killed on cancel */ }
    }

    // ---------------- JSONL parsing ----------------

    private List<NucleiFinding> ParseResults(string outFile, IProgress<string>? log)
    {
        var findings = new List<NucleiFinding>();
        if (!File.Exists(outFile)) return findings;

        foreach (var raw in File.ReadLines(outFile))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;

                var templateId = Str(root, "template-id") ?? "";

                JsonElement? info = root.TryGetProperty("info", out var infoEl) && infoEl.ValueKind == JsonValueKind.Object
                    ? infoEl : null;

                var name = info is { } i1 ? Str(i1, "name") ?? "" : "";
                var severity = ((info is { } i2 ? Str(i2, "severity") : null) ?? "unknown").ToUpperInvariant();
                var description = info is { } i3 ? Str(i3, "description") : null;

                var refs = info is { } i4 ? Arr(i4, "reference") : new List<string>();
                var cves = new List<string>();
                if (info is { } i5 && i5.TryGetProperty("classification", out var cls) && cls.ValueKind == JsonValueKind.Object)
                    cves = Arr(cls, "cve-id");

                var host = Str(root, "host") ?? "";
                var matchedAt = Str(root, "matched-at") ?? host;

                var extracted = Arr(root, "extract-results");
                if (extracted.Count == 0) extracted = Arr(root, "extract_results");

                var reference = refs.Count > 0 ? refs[0]
                    : cves.Count > 0 ? string.Join(", ", cves)
                    : null;

                findings.Add(new NucleiFinding(
                    templateId, name, severity, host, matchedAt,
                    extracted.Count > 0 ? string.Join(", ", extracted) : null,
                    description, reference, DateTime.Now));
            }
            catch (JsonException jex)
            {
                _log.Warn("nuclei", $"ligne de résultat mal formée ignorée : {jex.Message}");
            }
        }
        return findings;
    }

    private static string? Str(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object
           && obj.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static List<string> Arr(JsonElement obj, string name)
    {
        var res = new List<string>();
        if (obj.ValueKind != JsonValueKind.Object) return res;
        if (!obj.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return res;
        foreach (var el in v.EnumerateArray())
        {
            if (el.ValueKind == JsonValueKind.String)
            {
                var s = el.GetString();
                if (!string.IsNullOrWhiteSpace(s)) res.Add(s);
            }
            else if (el.ValueKind == JsonValueKind.Object
                     && el.TryGetProperty("value", out var sv)
                     && sv.ValueKind == JsonValueKind.String
                     && !string.IsNullOrWhiteSpace(sv.GetString()))
            {
                res.Add(sv.GetString()!);
            }
        }
        return res;
    }

    private static int SeverityRank(string s) => s.ToUpperInvariant() switch
    {
        "CRITICAL" => 5,
        "HIGH" => 4,
        "MEDIUM" => 3,
        "LOW" => 2,
        "INFO" => 1,
        _ => 0
    };
}
