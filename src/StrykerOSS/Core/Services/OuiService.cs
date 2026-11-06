using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using StrykerOSS.Core.Services;

namespace StrykerOSS.Core.Services;

/// <summary>
/// Real IEEE OUI (MAC vendor) lookup service.
/// Loads the bundled <c>Resources/oui.csv</c> (registry,assignment,"Organization Name",address).
/// If the bundled file is missing at runtime, downloads the official
/// https://standards-oui.ieee.org/oui/oui.csv into the app DataDir and loads it from there.
/// </summary>
public sealed class OuiService : IOuiService
{
    private const string IeeeOuiUrl = "https://standards-oui.ieee.org/oui/oui.csv";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };

    private readonly ISettings _settings;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, string> _cache = new();

    public OuiService(ISettings settings, ILogger log)
    {
        _settings = settings;
        _log = log;
    }

    public int EntryCount => _cache.Count;

    public Task InitializeAsync()
    {
        if (_cache.Count > 0) return Task.CompletedTask;
        return LoadAsync();
    }

    private async Task LoadAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_cache.Count > 0) return;

            var bundled = Path.Combine(AppContext.BaseDirectory, "Resources", "oui.csv");
            var path = File.Exists(bundled) ? bundled : await DownloadToDataDirAsync().ConfigureAwait(false);
            if (path == null)
            {
                _log.Warn("oui", $"OUI database unavailable: bundled file missing and download from {IeeeOuiUrl} failed.");
                return;
            }

            LoadCsv(path);
            _log.Info("oui", $"OUI database loaded: {EntryCount:N0} entries from {path}");
        }
        catch (Exception ex)
        {
            _log.Error("oui", "failed to initialize OUI database", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string?> DownloadToDataDirAsync()
    {
        try
        {
            var dir = _settings.EnsureDataDir();
            var target = Path.Combine(dir, "oui.csv");
            if (File.Exists(target)) return target;

            var tmp = target + ".download";
            using var resp = await Http.GetAsync(IeeeOuiUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await using var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
            await using var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await src.CopyToAsync(dst).ConfigureAwait(false);
            await dst.FlushAsync().ConfigureAwait(false);

            if (File.Exists(target)) File.Delete(target);
            File.Move(tmp, target);
            _log.Info("oui", $"downloaded IEEE OUI database ({new FileInfo(target).Length:N0} bytes) to {target}");
            return target;
        }
        catch (Exception ex)
        {
            _log.Error("oui", "download of IEEE OUI database failed", ex);
            return null;
        }
    }

    private void LoadCsv(string csvPath)
    {
        foreach (var line in File.ReadLines(csvPath))
        {
            if (line.Length < 10) continue;
            if (line.StartsWith("Registry,", StringComparison.OrdinalIgnoreCase)) continue; // header

            var fields = ParseCsvLine(line);
            if (fields.Count < 3) continue;

            var assignment = fields[1].Trim().ToUpperInvariant();
            if (assignment.Length != 6 || !assignment.All(char.IsAsciiHexDigit)) continue;

            var org = fields[2].Trim();
            if (org.Length == 0) continue;

            _cache[assignment] = org;
        }
    }

    /// <summary>
    /// Small quote-aware CSV field parser: handles "quoted, fields" and doubled quotes ("").
    /// </summary>
    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(c);
            }
            else
            {
                if (c == '"') inQuotes = true;
                else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }
        }
        fields.Add(sb.ToString());
        return fields;
    }

    public MacVendorInfo? Lookup(string mac)
    {
        if (_cache.Count == 0 || string.IsNullOrWhiteSpace(mac)) return null;

        var hex = new string(mac.Where(char.IsAsciiLetterOrDigit).ToArray()).ToUpperInvariant();
        if (hex.Length < 6) return null;
        hex = hex.Substring(0, 6);
        if (!hex.All(char.IsAsciiHexDigit)) return null;

        if (!_cache.TryGetValue(hex, out var vendor) || string.IsNullOrWhiteSpace(vendor)) return null;
        return new MacVendorInfo(mac, hex, vendor);
    }
}
