using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace StrykerOSS.Core.Services;

/// <summary>
/// Real IP geolocation via ip-api.com, KML/CSV exports and real WiGLE CSV import.
/// </summary>
public sealed class GeoMacService : IGeoMacService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly ILogger _log;

    public GeoMacService(ILogger log) => _log = log;

    public async Task<GeoIpInfo?> LookupIpAsync(string ip, CancellationToken ct = default)
    {
        try
        {
            ip = ip.Trim();
            var url = $"http://ip-api.com/json/{ip}?fields=66846719";
            using var resp = await Http.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            var status = GetStr(root, "status") ?? "";
            if (status == "fail") return null;

            return new GeoIpInfo(
                GetStr(root, "query") ?? ip,
                GetStr(root, "country") ?? "",
                GetStr(root, "regionName") ?? "",
                GetStr(root, "city") ?? "",
                GetStr(root, "isp") ?? "",
                GetStr(root, "org") ?? "",
                GetStr(root, "as") ?? "",
                GetDbl(root, "lat"),
                GetDbl(root, "lon"),
                GetStr(root, "timezone") ?? "");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.Error("geomac", $"IP lookup failed for {ip}", ex);
            return null;
        }
    }

    public async Task<string?> PublicIpAsync()
    {
        try
        {
            using var resp = await Http.GetAsync("http://ip-api.com/json/?fields=query");
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("query", out var q) && q.ValueKind == JsonValueKind.String
                ? q.GetString()
                : null;
        }
        catch (Exception ex)
        {
            _log.Error("geomac", "public IP query failed", ex);
            return null;
        }
    }

    public Task<string> ExportKmlAsync(IEnumerable<GeoPoint> pts, string path)
    {
        XNamespace ns = "http://www.opengis.net/kml/2.2";
        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement(ns + "kml",
                new XElement(ns + "Document",
                    new XElement(ns + "name", "StrykerOSS GeoMac export"),
                    new XElement(ns + "description", "Geo points exported by StrykerOSS (educational)."),
                    pts.Where(p => p.Lat.HasValue && p.Lon.HasValue).Select(p =>
                        new XElement(ns + "Placemark",
                            new XElement(ns + "name", p.Label),
                            new XElement(ns + "description", p.Bssid ?? p.Mac ?? p.Kind),
                            new XElement(ns + "Point",
                                new XElement(ns + "coordinates",
                                    $"{p.Lon!.Value.ToString(CultureInfo.InvariantCulture)},{p.Lat!.Value.ToString(CultureInfo.InvariantCulture)},0")))))));

        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        doc.Save(full);
        _log.Info("geomac", $"KML exported: {full} ({pts.Count(p => p.Lat.HasValue && p.Lon.HasValue)} placemarks)");
        return Task.FromResult(full);
    }

    public Task<string> ExportCsvAsync(IEnumerable<GeoPoint> pts, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("kind,label,bssid,mac,lat,lon,added");
        foreach (var p in pts)
        {
            sb.Append(p.Kind).Append(',')
              .Append(CsvEscape(p.Label)).Append(',')
              .Append(CsvEscape(p.Bssid ?? "")).Append(',')
              .Append(CsvEscape(p.Mac ?? "")).Append(',')
              .Append(p.Lat.HasValue ? p.Lat.Value.ToString(CultureInfo.InvariantCulture) : "").Append(',')
              .Append(p.Lon.HasValue ? p.Lon.Value.ToString(CultureInfo.InvariantCulture) : "").Append(',')
              .Append(p.Added.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
              .AppendLine();
        }
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(full, sb.ToString(), Encoding.UTF8);
        _log.Info("geomac", $"CSV exported: {full}");
        return Task.FromResult(full);
    }

    public Task<List<GeoPoint>> ImportWiGleCsvAsync(string path, IProgress<string>? log = null)
    {
        var result = new List<GeoPoint>();
        var lines = File.ReadAllLines(path);

        // skip preamble until the real WiGLE header line (contains the "MAC" column)
        List<string>? headers = null;
        int headerIndex = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains("MAC")) continue;
            headers = SplitCsvLine(lines[i]).Select(h => h.Trim()).ToList();
            headerIndex = i;
            break;
        }
        if (headers == null)
        {
            log?.Report("No WiGLE CSV header (MAC column) found - file skipped.");
            return Task.FromResult(result);
        }

        int ixMac = headers.FindIndex(h => h.Equals("MAC", StringComparison.OrdinalIgnoreCase));
        int ixSsid = headers.FindIndex(h => h.Equals("SSID", StringComparison.OrdinalIgnoreCase));
        int ixLat = headers.FindIndex(h => h.Equals("CurrentLatitude", StringComparison.OrdinalIgnoreCase));
        int ixLon = headers.FindIndex(h => h.Equals("CurrentLongitude", StringComparison.OrdinalIgnoreCase));
        int ixFirstSeen = headers.FindIndex(h => h.Equals("FirstSeen", StringComparison.OrdinalIgnoreCase)); // mapped, Added uses import time
        if (ixMac < 0 || ixLat < 0 || ixLon < 0)
        {
            log?.Report("WiGLE CSV is missing required MAC/CurrentLatitude/CurrentLongitude columns.");
            return Task.FromResult(result);
        }
        int maxIdx = new[] { ixMac, ixSsid, ixLat, ixLon, ixFirstSeen }.Max();

        for (int i = headerIndex + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;
            var cells = SplitCsvLine(line);
            if (cells.Count <= maxIdx) continue;

            var mac = cells[ixMac].Trim();
            if (mac.Length == 0) continue;
            if (!TryDbl(cells[ixLat], out var lat) || !TryDbl(cells[ixLon], out var lon)) continue; // skip bad rows

            var ssid = (ixSsid >= 0 ? cells[ixSsid].Trim() : "");
            result.Add(new GeoPoint(0, "wigle", ssid.Length > 0 ? ssid : mac, mac, mac, lat, lon, DateTime.Now));
        }

        log?.Report($"Parsed {result.Count} WiGLE point(s) from {Path.GetFileName(path)}.");
        _log.Info("geomac", $"WiGLE import {path}: {result.Count} points");
        return Task.FromResult(result);
    }

    // ---- helpers ----

    private static string? GetStr(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double GetDbl(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0d;

    private static bool TryDbl(string s, out double value)
        => double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static string CsvEscape(string s)
    {
        if (s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r'))
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }

    /// <summary>Real RFC4180-ish CSV splitter (handles quoted cells and doubled quotes, used by real WiGLE exports).</summary>
    private static List<string> SplitCsvLine(string line)
    {
        var cells = new List<string>();
        var cur = new StringBuilder();
        var inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                    else inQuotes = false;
                }
                else cur.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') { cells.Add(cur.ToString()); cur.Clear(); }
            else cur.Append(c);
        }
        cells.Add(cur.ToString());
        return cells;
    }
}
