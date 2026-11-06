using System.Collections.Concurrent;
using System.Text;

namespace StrykerOSS.Core.Services;

/// <summary>
/// REAL pcap / pcapng parser for WPA handshake analysis (Windows port of the
/// strykerapp handshake module). Walks the exact captured bytes, extracts
/// EAPOL M1-M4 key frames (nonces, MICs, replay data, PMKID KDEs) and converts
/// the best AP/client pair to hashcat 22000 hash lines.
/// Educational use only - GPL-3.0.
/// </summary>
public sealed class HandshakeService : IHandshakeService
{
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<string, ParsedCapture> _cache = new();
    private static readonly char[] InvalidFileChars = Path.GetInvalidFileNameChars();
    private const string BroadcastMac = "FF:FF:FF:FF:FF:FF";

    public HandshakeService(ILogger log) => _log = log;

    // ============================ parse model ============================

    private sealed class ParsedCapture
    {
        public string FilePath = "";
        public int PacketCount;
        public int EapolCount;
        public readonly Dictionary<string, string> SsidByBssid = new();   // BSSID -> SSID (from beacons)
        public readonly Dictionary<string, Session> Sessions = new();     // "AP|CLIENT" -> session
        public readonly List<uint> InterfaceLinkTypes = new();            // pcapng IDB linktypes / pcap global

        public Session? Best()
        {
            Session? best = null;
            foreach (var s in Sessions.Values)
                if (best == null || s.Score() > best.Score()) best = s;
            return best;
        }

        public List<string> ClientsOf(string ap) =>
            Sessions.Values.Where(x => x.Ap == ap).Select(x => x.Client).Distinct().ToList();
    }

    private sealed class Session
    {
        public string Ap = "";
        public string Client = "";
        public bool M1, M2, M3, M4;
        public byte[]? ApNonce;      // from M1/M3
        public byte[]? StaNonce;     // from M2/M4
        public byte[]? Mic;          // 16 bytes from M2/M4
        public byte[]? Eapol;        // full captured client EAPOL pdu (M2 preferred, else M4)
        public int EapolMsg;         // 2 or 4 - which message Eapol came from
        public byte[]? Pmkid;        // 16 bytes from unencrypted M1 key data (PMKID KDE)
        public int Frames;           // EAPOL key frames seen for this pair

        public bool HasHandshake => (M1 && M2) || (M2 && M3) || (M3 && M4);

        public int Score()
        {
            int v = Frames;
            if (HasHandshake) v += 1000;
            if (Eapol != null && Mic != null && ApNonce != null && StaNonce != null) v += 500;
            return v;
        }
    }

    // ============================ IHandshakeService ============================

    public async Task<HandshakeInfo> AnalyzeAsync(string pcapPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pcapPath)) throw new ArgumentException("Capture path required", nameof(pcapPath));
        if (!File.Exists(pcapPath)) throw new FileNotFoundException("Capture file not found", pcapPath);

        var parsed = await Task.Run(() =>
        {
            var c = ParseFile(pcapPath, ct);
            _cache[Key(pcapPath)] = c;
            return c;
        }, ct).ConfigureAwait(false);

        var best = parsed.Best();
        string? ssid = null, bssid = null;
        var clients = new List<string>();
        int frames = 0;
        bool hasHandshake = false;
        if (best != null)
        {
            bssid = best.Ap;
            parsed.SsidByBssid.TryGetValue(best.Ap, out ssid);
            hasHandshake = best.HasHandshake;
            frames = best.Frames;
            clients = parsed.ClientsOf(best.Ap);
            if (!clients.Contains(best.Client)) clients.Insert(0, best.Client);
        }

        var info = new HandshakeInfo(0, pcapPath, ssid, bssid, frames, hasHandshake, clients, DateTime.Now);
        _log.Info("handshakes",
            $"{Path.GetFileName(pcapPath)}: {parsed.PacketCount} packets, {parsed.EapolCount} EAPOL frames, " +
            $"handshake={(hasHandshake ? "COMPLETE" : "INCOMPLETE")}" + (ssid != null ? $", ssid={ssid}" : ""));
        return info;
    }

    public async Task<List<HandshakeInfo>> AnalyzeDirectoryAsync(string dir, IProgress<string>? log = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(dir)) throw new ArgumentException("Folder required", nameof(dir));
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException($"Folder not found: {dir}");

        string[] exts = { ".pcap", ".cap", ".pcapng" };
        var files = Directory.EnumerateFiles(dir)
            .Where(f => exts.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        log?.Report($"Found {files.Count} capture file(s) in {dir}");
        var result = new List<HandshakeInfo>();
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var info = await AnalyzeAsync(f, ct).ConfigureAwait(false);
                result.Add(info);
                log?.Report($"{Path.GetFileName(f)}: eapol={info.EapolFrames} handshake={(info.HasHandshake ? "COMPLETE" : "INCOMPLETE")}");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.Error("handshakes", $"failed to parse {f}", ex);
                log?.Report($"ERROR {Path.GetFileName(f)}: {ex.Message}");
            }
        }
        return result;
    }

    public async Task<string> ExportHashcat22000Async(HandshakeInfo h, string outDir)
    {
        if (h is null) throw new ArgumentNullException(nameof(h));
        if (string.IsNullOrWhiteSpace(outDir)) throw new ArgumentException("Output directory required", nameof(outDir));
        if (!File.Exists(h.FilePath)) throw new FileNotFoundException("Capture file not found", h.FilePath);

        // reuse the cached parse (exact captured bytes) or re-parse the real file
        var parsed = await Task.Run(() =>
            _cache.TryGetValue(Key(h.FilePath), out var c) ? c : ParseFile(h.FilePath, CancellationToken.None)).ConfigureAwait(false);

        var s = parsed.Best() ?? throw new InvalidOperationException("No EAPOL frames found in this capture.");
        if (s.Eapol == null || s.Mic == null || s.ApNonce == null || s.StaNonce == null)
            throw new InvalidOperationException("Capture lacks M1 nonce + M2/M4 MIC material required for a 22000 hash.");

        // real hashcat 22000 message-pair code (0=M1M2, 2=M2M3, 3=M2M4, 4=M3M4, 1=M1M4)
        int msgPair = s.EapolMsg == 2 ? (s.M1 ? 0 : s.M3 ? 2 : 3) : (s.M1 ? 1 : s.M3 ? 4 : 3);

        var lines = new List<string>
        {
            // WPA*02*<mic>*<ap mac>*<ap nonce>*<sta mac>*<sta nonce>*<eapol as captured>*<message pair>
            $"WPA*02*{Hex(s.Mic)}*{NoColon(s.Ap)}*{Hex(s.ApNonce)}*{NoColon(s.Client)}*{Hex(s.StaNonce)}*{Hex(s.Eapol)}*{msgPair}"
        };
        if (s.Pmkid != null)
        {
            // canonical PMKID-only line: WPA*01*<pmkid>*<ap mac>*<sta mac>* (empty 5th field)
            lines.Add($"WPA*01*{Hex(s.Pmkid)}*{NoColon(s.Ap)}*{NoColon(s.Client)}*");
        }

        Directory.CreateDirectory(outDir);
        string baseName = parsed.SsidByBssid.TryGetValue(s.Ap, out var ssid) && !string.IsNullOrWhiteSpace(ssid)
            ? SanitizeFileName(ssid)
            : NoColon(s.Ap);
        string outPath = Path.Combine(outDir, baseName + ".22000.txt");
        await File.WriteAllTextAsync(outPath, string.Join(Environment.NewLine, lines) + Environment.NewLine).ConfigureAwait(false);
        _log.Info("handshakes", $"exported {lines.Count} hashcat 22000 line(s) -> {outPath}");
        return outPath;
    }

    public async Task<string> ExportCsvAsync(IEnumerable<HandshakeInfo> list, string outPath)
    {
        if (list is null) throw new ArgumentNullException(nameof(list));
        if (string.IsNullOrWhiteSpace(outPath)) throw new ArgumentException("Output path required", nameof(outPath));

        var sb = new StringBuilder();
        sb.AppendLine("file,ssid,bssid,eapol_frames,has_handshake,clients");
        int rows = 0;
        foreach (var h in list)
        {
            sb.AppendLine(string.Join(",",
                Csv(h.FilePath), Csv(h.Ssid), Csv(h.Bssid),
                h.EapolFrames.ToString(), h.HasHandshake ? "true" : "false",
                Csv(string.Join(";", h.ClientMacs))));
            rows++;
        }
        var dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(outPath, sb.ToString()).ConfigureAwait(false);
        _log.Info("handshakes", $"exported CSV ({rows} rows) -> {outPath}");
        return outPath;
    }

    // ============================ container parsing ============================

    private ParsedCapture ParseFile(string path, CancellationToken ct)
    {
        byte[] d = File.ReadAllBytes(path);
        if (d.Length < 24) throw new InvalidDataException($"File too small to be a capture ({d.Length} bytes): {path}");

        var cap = new ParsedCapture { FilePath = path };
        uint magic = ReadU32(d, 0, le: true);
        if (magic == 0x0A0D0D0A)
            ParsePcapng(d, cap, ct);                                   // pcapng (SHB block type magic)
        else if (magic == 0xA1B2C3D4 || magic == 0xD4C3B2A1)
            ParsePcap(d, cap, ct);                                     // classic pcap
        else
            throw new InvalidDataException($"Unsupported capture magic 0x{magic:X8}: {path}");
        return cap;
    }

    private void ParsePcap(byte[] d, ParsedCapture cap, CancellationToken ct)
    {
        // magic read little-endian: 0xA1B2C3D4 => bytes on disk D4 C3 B2 A1 => little-endian file
        bool le = ReadU32(d, 0, le: true) == 0xA1B2C3D4;
        uint linktype = ReadU32(d, 20, le);                            // global header linktype @20
        cap.InterfaceLinkTypes.Add(linktype);

        int off = 24;                                                  // 24-byte global header
        while (off + 16 <= d.Length)
        {
            ct.ThrowIfCancellationRequested();
            uint incl = ReadU32(d, off + 8, le);                       // packet header: ts_sec@0 ts_usec@4 incl_len@8 orig_len@12
            if (incl == 0 || incl > (1u << 20) || off + 16 + incl > d.Length) break;
            HandlePacket(d, off + 16, (int)incl, linktype, cap);
            off += 16 + (int)incl;
            cap.PacketCount++;
        }
    }

    private void ParsePcapng(byte[] d, ParsedCapture cap, CancellationToken ct)
    {
        // SHB byte-order magic 0x1A2B3C4D at offset 8 decides block field endianness
        bool le = ReadU32(d, 8, le: true) == 0x1A2B3C4D;
        int off = 0;
        while (off + 12 <= d.Length)
        {
            ct.ThrowIfCancellationRequested();
            uint total = ReadU32(d, off + 4, le);                      // block total length @+4 (includes 4-byte padding)
            if (total < 12 || total > (1u << 24) || off + total > d.Length) break;
            uint type = ReadU32(d, off, le);

            if (type == 0x00000001)                                    // Interface Description Block
            {
                uint lt = ReadU16(d, off + 8, le);                     // linktype uint16 @8
                cap.InterfaceLinkTypes.Add(lt);
            }
            else if (type == 0x00000003)                               // Enhanced Packet Block
            {
                uint ifId = ReadU32(d, off + 8, le);
                uint capLen = ReadU32(d, off + 20, le);                // ts_hi@12 ts_lo@16 cap_len@20 orig_len@24 data@28
                uint lt = ifId < (uint)cap.InterfaceLinkTypes.Count ? cap.InterfaceLinkTypes[(int)ifId] : 1u;
                if (capLen > 0 && capLen <= total - 28 && off + 28 + capLen <= d.Length)
                {
                    HandlePacket(d, off + 28, (int)capLen, lt, cap);
                    cap.PacketCount++;
                }
            }
            // all other blocks (SHB, NRB, ISB, ...) skipped by declared length
            off += (int)total;
        }
    }

    private void HandlePacket(byte[] d, int off, int len, uint linktype, ParsedCapture cap)
    {
        switch (linktype)
        {
            case 1:                                                    // Ethernet
                HandleEthernet(d, off, len, cap);
                break;
            case 105:                                                  // raw 802.11
                Handle80211(d, off, len, cap);
                break;
            case 127:                                                  // radiotap + 802.11
                if (len >= 4)
                {
                    int hlen = ReadU16(d, off + 2, le: true);          // radiotap: version@0 pad@1 header_len@2 (LE)
                    if (hlen >= 4 && hlen < len) Handle80211(d, off + hlen, len - hlen, cap);
                }
                break;
        }
    }

    // ============================ link layer ============================

    private void HandleEthernet(byte[] d, int off, int len, ParsedCapture cap)
    {
        if (len < 14) return;
        uint et = ReadU16(d, off + 12, le: false);                     // dst@0 src@6 ethertype@12 (BE)
        int payload = off + 14;
        if (et == 0x8100)                                              // 802.1Q VLAN tag (+4)
        {
            if (len < 18) return;
            et = ReadU16(d, off + 16, le: false);
            payload = off + 18;
        }
        if (et != 0x888E) return;                                      // EAPOL over Ethernet
        if (payload >= off + len) return;
        // wired capture: no DS bits - direction is derived from EAPOL semantics
        HandleEapol(d, payload, off + len - payload, Mac(d, off + 6) /*src*/, Mac(d, off) /*dst*/, fromDs: null, cap);
    }

    private void Handle80211(byte[] d, int off, int len, ParsedCapture cap)
    {
        if (len < 24) return;
        int fc0 = d[off], fc1 = d[off + 1];
        int type = (fc0 >> 2) & 0x03;                                  // 0 mgmt, 1 ctrl, 2 data
        int subtype = (fc0 >> 4) & 0x0F;
        bool toDs = (fc1 & 0x01) != 0;
        bool fromDs = (fc1 & 0x02) != 0;
        bool protectedFrame = (fc1 & 0x40) != 0;

        string a1 = Mac(d, off + 4);
        string a2 = Mac(d, off + 10);
        string a3 = Mac(d, off + 16);

        if (type == 0)                                                 // management frames carry the SSID
        {
            if (subtype == 8 || subtype == 5)                          // beacon / probe response
                ParseSsidIe(d, off, len, a3 != BroadcastMac ? a3 : a2, cap);
            return;
        }
        if (type != 2 || protectedFrame) return;                       // only unencrypted data can carry EAPOL

        int hdr = 24;                                                  // fc+dur+3 addr+seq
        if (toDs && fromDs) hdr += 6;                                  // addr4 (WDS / 4-address, rare)
        if ((subtype & 0x08) != 0) hdr += 2;                           // QoS control
        if (len <= hdr + 8) return;
        int p = off + hdr;
        if (d[p] != 0xAA || d[p + 1] != 0xAA || d[p + 2] != 0x03 || d[p + 3] != 0 || d[p + 4] != 0 || d[p + 5] != 0)
            return;                                                    // not LLC/SNAP (AA AA 03 00 00 00)
        uint et = ReadU16(d, p + 6, le: false);                        // SNAP ethertype
        if (et != 0x888E) return;
        int eapolOff = p + 8;
        // 802.11: fromDS -> addr2=BSSID(AP)/addr1=DA(client); toDS -> addr1=RA=BSSID(AP)/addr2=TA(client)
        HandleEapol(d, eapolOff, off + len - eapolOff, a2, a1, fromDs, cap);
    }

    private void ParseSsidIe(byte[] d, int off, int len, string bssid, ParsedCapture cap)
    {
        if (len < 36) return;                                          // ts(8)+interval(2)+cap(2) after 24-byte hdr
        int i = off + 36;
        int end = off + len;
        while (i + 2 <= end)
        {
            int t = d[i];
            int l = d[i + 1];
            if (i + 2 + l > end) break;
            if (t == 0)                                                // SSID IE
            {
                var ssid = CleanSsid(d, i + 2, l);
                if (ssid != null) cap.SsidByBssid[bssid] = ssid;
                return;
            }
            i += 2 + l;
        }
    }

    private static string? CleanSsid(byte[] d, int off, int len)
    {
        if (len <= 0 || len > 32) return null;                         // hidden or malformed
        var sb = new StringBuilder(len);
        for (int i = 0; i < len; i++)
        {
            byte c = d[off + i];
            if (c < 32 || c > 126) return null;                        // binary SSID - BSSID is used instead
            sb.Append((char)c);
        }
        return sb.ToString();
    }

    // ============================ EAPOL ============================

    private void HandleEapol(byte[] d, int off, int len, string src, string dst, bool? fromDs, ParsedCapture cap)
    {
        if (len < 99) return;                                          // smallest EAPOL-Key pdu is 99 bytes
        if (d[off + 1] != 3) return;                                   // EAPOL type 3 = EAPOL-Key
        int pduLen = 4 + ReadU16(d, off + 2, le: false);               // EAPOL length field (BE) covers the key body
        if (pduLen < 99) return;
        if (pduLen > len) pduLen = len;

        ushort ki = ReadU16(d, off + 5, le: true);                     // Key Information (LE)
        bool ack = (ki & 0x80) != 0;                                   // bit 7  KeyAck
        bool mic = (ki & 0x100) != 0;                                  // bit 8  KeyMIC
        bool secure = (ki & 0x200) != 0;                               // bit 9  Secure
        bool encrypted = (ki & 0x1000) != 0;                           // bit 12 EncryptedKeyData

        bool fromAp;
        int msg;
        if (fromDs.HasValue)
        {
            fromAp = fromDs.Value;
            if (fromAp) msg = mic ? 3 : (ack ? 1 : 0);                 // M1: fromDS, Ack=1, MIC=0 | M3: fromDS, MIC=1, Ack=1
            else msg = mic && !ack ? (secure ? 4 : 2) : 0;             // M2/M4: toDS, MIC=1, Ack=0 (Secure bit separates them)
        }
        else
        {
            // Ethernet capture: M1/M3 originate at the AP, M2/M4 at the client
            if (!mic && ack) { msg = 1; fromAp = true; }
            else if (mic && ack) { msg = 3; fromAp = true; }
            else if (mic && !ack) { msg = secure ? 4 : 2; fromAp = false; }
            else return;
        }
        if (msg == 0) return;

        string ap = fromAp ? src : dst;
        string client = fromAp ? dst : src;
        var pdu = Slice(d, off, pduLen);
        cap.EapolCount++;

        string key = ap + "|" + client;
        if (!cap.Sessions.TryGetValue(key, out var s))
        {
            s = new Session { Ap = ap, Client = client };
            cap.Sessions[key] = s;
        }
        s.Frames++;
        switch (msg)
        {
            case 1:                                                    // M1: AP nonce @17..48, PMKID KDE may follow unencrypted
                s.M1 = true;
                s.ApNonce ??= Slice(pdu, 17, 32);
                if (!encrypted) s.Pmkid ??= ExtractPmkid(pdu);
                break;
            case 2:                                                    // M2: STA nonce, MIC, full pdu kept for 22000 export
                s.M2 = true;
                s.StaNonce = Slice(pdu, 17, 32);
                s.Mic = Slice(pdu, 81, 16);
                s.Eapol = pdu;
                s.EapolMsg = 2;
                break;
            case 3:
                s.M3 = true;
                s.ApNonce = Slice(pdu, 17, 32);
                break;
            case 4:
                s.M4 = true;
                s.StaNonce = Slice(pdu, 17, 32);
                s.Mic = Slice(pdu, 81, 16);
                if (s.Eapol == null) { s.Eapol = pdu; s.EapolMsg = 4; }
                break;
        }
    }

    /// <summary>Walks unencrypted Key Data KDEs for the PMKID KDE (DD 14 00 0F AC 04 + 16 bytes).</summary>
    private static byte[]? ExtractPmkid(byte[] pdu)
    {
        int kdLen = ReadU16(pdu, 97, le: true);                        // Key Data Length @97 (LE)
        int kdOff = 99;                                                // Key Data @99
        int kdEnd = Math.Min(pdu.Length, kdOff + kdLen);
        int p = kdOff;
        while (p + 2 <= kdEnd)
        {
            int t = pdu[p];
            int l = pdu[p + 1];
            if (p + 2 + l > kdEnd) break;
            if (t == 0xDD && l >= 20 && pdu[p + 2] == 0x00 && pdu[p + 3] == 0x0F && pdu[p + 4] == 0xAC && pdu[p + 5] == 0x04)
                return Slice(pdu, p + 6, 16);                          // real PMKID (clientless attack material)
            p += 2 + l;
        }
        return null;
    }

    // ============================ helpers ============================

    private static string Key(string path) => Path.GetFullPath(path).ToLowerInvariant();

    private static uint ReadU32(byte[] d, int o, bool le) => le
        ? (uint)(d[o] | d[o + 1] << 8 | d[o + 2] << 16 | d[o + 3] << 24)
        : (uint)(d[o] << 24 | d[o + 1] << 16 | d[o + 2] << 8 | d[o + 3]);

    private static ushort ReadU16(byte[] d, int o, bool le) => le
        ? (ushort)(d[o] | d[o + 1] << 8)
        : (ushort)(d[o] << 8 | d[o + 1]);

    private static byte[] Slice(byte[] d, int off, int len)
    {
        var r = new byte[len];
        Buffer.BlockCopy(d, off, r, 0, len);
        return r;
    }

    private static string Mac(byte[] d, int off) =>
        $"{d[off]:X2}:{d[off + 1]:X2}:{d[off + 2]:X2}:{d[off + 3]:X2}:{d[off + 4]:X2}:{d[off + 5]:X2}";

    private static string Hex(byte[] b)
    {
        var sb = new StringBuilder(b.Length * 2);
        foreach (var x in b) sb.Append(x.ToString("x2"));
        return sb.ToString();
    }

    private static string NoColon(string mac) => mac.Replace(":", "");

    private static string SanitizeFileName(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s)
            if (Array.IndexOf(InvalidFileChars, c) < 0 && c >= 32 && c <= 126) sb.Append(c);
        return sb.Length == 0 ? "capture" : sb.ToString();
    }

    private static string Csv(string? v)
    {
        v ??= "";
        if (!(v.Contains(',') || v.Contains('"') || v.Contains('\n') || v.Contains('\r'))) return v;
        return "\"" + v.Replace("\"", "\"\"") + "\"";
    }
}
