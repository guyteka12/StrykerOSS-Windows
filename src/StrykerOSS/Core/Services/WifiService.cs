using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Xml.Linq;

namespace StrykerOSS.Core.Services;

// ============================================================
// WifiService - REAL Wi-Fi control via the Windows Native Wifi API
// (wlanapi.dll). Every call talks to the WLAN AutoConfig service of
// this machine and its built-in wireless adapter - no simulation,
// no external hardware.
//
// Layout notes (verified against wlanapi.h / MSDN):
//  - WLAN_INTERFACE_INFO_LIST / WLAN_AVAILABLE_NETWORK_LIST / WLAN_BSS_LIST
//    all begin with DWORD counters followed by an inline array. The C#
//    {uint, uint, ..., IntPtr} declaration makes the IntPtr field land
//    exactly where the native inline array starts (offset 8 on x64).
//  - WLAN_AVAILABLE_NETWORK field order (MSDN): dot11Ssid, dot11BssType,
//    uNumberOfBssids, bNetworkConnectable, wlanNotConnectableReason,
//    uNumberOfPhyTypes, dot11PhyTypes[8], bMorePhyTypes, wlanSignalQuality,
//    bSecurityEnabled, dot11AuthAlgo, dot11CipherAlgo, dwFlags, dwReserved.
//  - WLAN_BSS_ENTRY (x64): dot11Ssid@0(36) uPhyId@36 dot11Bssid@40(6)
//    dot11BssType@48 dot11BssPhyType@52 lRssi@56 uLinkQuality@60
//    bInRegDomain@64(BOOL) usBeaconPeriod@68 usCapabilityInformation@70
//    ulTimestamp@72(8) ulHostTimestamp@80(8) ulChCenterFrequency@88(MHz)
//    wlanRateSet@92(4 + 126*2 = 260) ulIeOffset@348 ulIeSize@352
//    (fixed size 356 -> padded to 360). BSS entries are walked defensively:
//    each candidate header is validated before it is trusted.
//  - WLAN_CONNECTION_ATTRIBUTES (opcode wlan_intf_opcode_current_connection
//    = 7) is read by documented offsets only: isState@0, mode@4,
//    strProfileName@8 (WCHAR[256] = 512 bytes), association SSID
//    uSSIDLength@520 / bytes@524, dot11BssType@556, dot11Bssid@560(6).
// ============================================================
public sealed class WifiService : IWifiService
{
    private const uint ClientVersion = 2;

    private static readonly string[] InterfaceStates =
        { "not ready", "connected", "disconnected", "authenticating", "associating", "discovering", "disconnecting" };

    private static string StateName(uint state)
        => state < (uint)InterfaceStates.Length ? InterfaceStates[state] : $"state {state}";

    private static void Throw(uint rc, string op)
    {
        if (rc != 0)
            throw new InvalidOperationException($"{op} failed with Win32 error {rc}.");
    }

    private static IntPtr OpenHandle()
    {
        uint rc = WlanOpenHandle(ClientVersion, IntPtr.Zero, out _, out IntPtr h);
        if (rc != 0)
            throw new InvalidOperationException(
                $"WlanOpenHandle failed (Win32 error {rc}) - is the WLAN AutoConfig service (WlanSvc) running?");
        return h;
    }

    // ===================== native interop =====================

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WLAN_INTERFACE_INFO
    {
        public Guid InterfaceGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string InterfaceDescription;
        public uint isState; // WLAN_INTERFACE_STATE
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WLAN_INTERFACE_INFO_LIST
    {
        public uint dwNumberOfItems;
        public uint dwIndex;
        public IntPtr InterfaceInfo; // native inline array starts here
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DOT11_SSID
    {
        public uint uSSIDLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] ucSSID;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WLAN_AVAILABLE_NETWORK
    {
        public DOT11_SSID dot11Ssid;
        public uint dot11BssType;
        public uint uNumberOfBssids;
        public uint bNetworkConnectable;
        public uint wlanNotConnectableReason;
        public uint uNumberOfPhyTypes;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public byte[] dot11PhyTypes;
        public uint bMorePhyTypes;
        public uint wlanSignalQuality;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bSecurityEnabled;
        public uint dot11AuthAlgo;
        public uint dot11CipherAlgo;
        public uint dwFlags; // bit 0 = WLAN_AVAILABLE_NETWORK_CONNECTED
        public uint dwReserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WLAN_AVAILABLE_NETWORK_LIST
    {
        public uint dwNumberOfItems;
        public uint dwIndex;
        public IntPtr Network; // native inline array starts here
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WLAN_RATE_SET
    {
        public uint uRateSetLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 126)]
        public ushort[] usRateSet;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WLAN_BSS_ENTRY
    {
        public DOT11_SSID dot11Ssid;           // 0
        public uint uPhyId;                    // 36
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
        public byte[] dot11Bssid;              // 40
        public uint dot11BssType;              // 48
        public uint dot11BssPhyType;           // 52
        public int lRssi;                      // 56
        public uint uLinkQuality;              // 60
        public uint bInRegDomain;              // 64 (BOOL)
        public ushort usBeaconPeriod;          // 68
        public ushort usCapabilityInformation; // 70
        public ulong ulTimestamp;              // 72
        public ulong ulHostTimestamp;          // 80
        public uint ulChCenterFrequency;       // 88 (MHz)
        public WLAN_RATE_SET wlanRateSet;      // 92
        public uint ulIeOffset;                // 348
        public uint ulIeSize;                  // 352
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WLAN_BSS_LIST
    {
        public uint dwTotalSize;
        public uint dwNumberOfItems;
        public uint dwIndex;
        public IntPtr wlanBssEntries; // native inline array starts here (x64)
    }

    // Native: { DWORD count; DWORD index; WLAN_PROFILE_INFO items[1]; }
    // where WLAN_PROFILE_INFO = { WCHAR name[256]; DWORD flags; } -> 516 bytes/item.
    [StructLayout(LayoutKind.Sequential)]
    private struct WLAN_PROFILE_INFO_LIST
    {
        public uint dwNumberOfItems;
        public uint dwIndex;
        public IntPtr ProfileInfo; // native inline array starts here
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WLAN_PROFILE_INFO
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strProfileName;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WLAN_CONNECTION_PARAMETERS
    {
        public uint wlanConnectionMode; // 0 = wlan_connection_mode_profile
        [MarshalAs(UnmanagedType.LPWStr)]
        public string strProfile;
        public uint dot11BssType; // 3 = dot11_BSS_type_any
        public uint dwFlags;
        public IntPtr pDot11Ssid;
    }

    [DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(uint dwClientVersion, IntPtr pReserved,
        out uint pdwNegotiatedVersion, out IntPtr phClientHandle);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(IntPtr hClientHandle, IntPtr pReserved);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr pMemory);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanEnumInterfaces(IntPtr hClientHandle, IntPtr pReserved,
        out IntPtr ppInterfaceList);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanScan(IntPtr hClientHandle, ref Guid pInterfaceGuid,
        IntPtr pDot11Ssid, IntPtr pIeData, IntPtr pReserved);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanGetAvailableNetworkList(IntPtr hClientHandle, ref Guid pInterfaceGuid,
        uint dwFlags, IntPtr pReserved, out IntPtr ppAvailableNetworkList);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanGetNetworkBssList(IntPtr hClientHandle, ref Guid pInterfaceGuid,
        IntPtr pDot11Ssid, uint dot11BssType, [MarshalAs(UnmanagedType.Bool)] bool bSecurityEnabled,
        IntPtr pReserved, out IntPtr ppWlanBssList);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanQueryInterface(IntPtr hClientHandle, ref Guid pInterfaceGuid,
        uint opCode, IntPtr pReserved, out uint pdwDataSize, ref IntPtr ppData, IntPtr pWlanOpcodeValueType);

    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)]
    private static extern uint WlanSetProfile(IntPtr hClientHandle, ref Guid pInterfaceGuid,
        uint dwFlags, string strProfileXml, string? strAllUserProfileSecurity,
        [MarshalAs(UnmanagedType.Bool)] bool bOverwrite, IntPtr pReserved, out uint pdwReason);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanGetProfileList(IntPtr hClientHandle, ref Guid pInterfaceGuid,
        IntPtr pReserved, out IntPtr ppProfileList);

    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)]
    private static extern uint WlanGetProfile(IntPtr hClientHandle, ref Guid pInterfaceGuid,
        string strProfileName, IntPtr pReserved, out IntPtr pstrProfileXml, IntPtr pdwFlags,
        out uint pdwGrantedAccess);

    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)]
    private static extern uint WlanDeleteProfile(IntPtr hClientHandle, ref Guid pInterfaceGuid,
        string strProfileName, IntPtr pReserved);

    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)]
    private static extern uint WlanConnect(IntPtr hClientHandle, ref Guid pInterfaceGuid,
        ref WLAN_CONNECTION_PARAMETERS pConnectionParameters, IntPtr pReserved);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanDisconnect(IntPtr hClientHandle, ref Guid pInterfaceGuid,
        IntPtr pReserved);

    // ===================== IWifiService =====================

    public Task<List<WifiAdapterInfo>> GetAdaptersAsync() => Task.Run(GetAdaptersCore);

    public Task<WifiAdapterInfo?> GetCurrentConnectionAsync()
        => Task.Run(() => GetAdaptersCore().FirstOrDefault(a => a.Connected));

    public Task<List<WifiNetwork>> ScanAsync(IProgress<string>? log = null, CancellationToken ct = default)
        => Task.Run(() => ScanCore(log, ct), ct);

    public async Task<bool> ConnectAsync(WifiConnectRequest req, CancellationToken ct = default)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Ssid))
            throw new ArgumentException("SSID is required.", nameof(req));
        return await Task.Run(() => ConnectCoreAsync(req, ct), ct);
    }

    public Task DisconnectAsync() => Task.Run(DisconnectCore);

    public Task<List<(string Name, string Auth)>> GetProfilesAsync() => Task.Run(GetProfilesCore);

    public Task<bool> DeleteProfileAsync(string name) => Task.Run(() => DeleteProfileCore(name));

    // ===================== adapters =====================

    private List<WifiAdapterInfo> GetAdaptersCore()
    {
        IntPtr h = OpenHandle();
        try
        {
            var ifaces = EnumInterfaces(h);
            NetworkInterface[] nics;
            try { nics = NetworkInterface.GetAllNetworkInterfaces(); }
            catch { nics = Array.Empty<NetworkInterface>(); }

            var result = new List<WifiAdapterInfo>();
            foreach (var ifc in ifaces)
            {
                var g = ifc.InterfaceGuid;
                string guidBraced = g.ToString("B");
                var nic = nics.FirstOrDefault(n =>
                    string.Equals(n.Id, guidBraced, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(n.Id.Replace("{", "").Replace("}", ""), g.ToString("D"), StringComparison.OrdinalIgnoreCase));

                var conn = QueryConnection(h, g);
                bool connected = ifc.isState == 1; // wlan_interface_state_connected
                string? ssid = connected && conn.HasValue && conn.Value.Ssid.Length > 0 ? conn.Value.Ssid : null;
                string? bssid = connected && conn.HasValue ? conn.Value.Bssid : null;

                result.Add(new WifiAdapterInfo(
                    nic?.Name ?? guidBraced,
                    string.IsNullOrWhiteSpace(ifc.InterfaceDescription) ? (nic?.Description ?? "Wi-Fi adapter") : ifc.InterfaceDescription,
                    g.ToString("D"),
                    StateName(ifc.isState),
                    connected,
                    ssid,
                    bssid));
            }
            return result;
        }
        finally { WlanCloseHandle(h, IntPtr.Zero); }
    }

    private List<WLAN_INTERFACE_INFO> EnumInterfaces(IntPtr h)
    {
        Throw(WlanEnumInterfaces(h, IntPtr.Zero, out IntPtr listPtr), "WlanEnumInterfaces");
        try
        {
            var list = Marshal.PtrToStructure<WLAN_INTERFACE_INFO_LIST>(listPtr);
            int stride = Marshal.SizeOf<WLAN_INTERFACE_INFO>();
            var result = new List<WLAN_INTERFACE_INFO>((int)list.dwNumberOfItems);
            for (int i = 0; i < list.dwNumberOfItems; i++)
                result.Add(Marshal.PtrToStructure<WLAN_INTERFACE_INFO>(list.InterfaceInfo + i * stride));
            return result;
        }
        finally { WlanFreeMemory(listPtr); }
    }

    /// <summary>
    /// WlanQueryInterface(opcode 7 = wlan_intf_opcode_current_connection) returns a
    /// WLAN_CONNECTION_ATTRIBUTES blob. Only the first, documented fields are parsed
    /// by offset: isState@0, mode@4, strProfileName@8(512), ssidLen@520, ssid@524,
    /// dot11BssType@556, dot11Bssid@560(6).
    /// </summary>
    private static (uint State, string Profile, string Ssid, string Bssid)? QueryConnection(IntPtr h, Guid g)
    {
        IntPtr data = IntPtr.Zero;
        try
        {
            uint rc = WlanQueryInterface(h, ref g, 7 /* wlan_intf_opcode_current_connection */,
                IntPtr.Zero, out uint size, ref data, IntPtr.Zero);
            if (rc != 0 || data == IntPtr.Zero || size < 566) return null;

            uint state = (uint)Marshal.ReadInt32(data, 0);
            string profile = Marshal.PtrToStringUni(data + 8, 256)?.TrimEnd('\0') ?? "";

            uint ssidLen = (uint)Marshal.ReadInt32(data, 520);
            if (ssidLen > 32) ssidLen = 32;
            var ssidBytes = new byte[ssidLen];
            if (ssidLen > 0) Marshal.Copy(data + 524, ssidBytes, 0, (int)ssidLen);

            var bssidBytes = new byte[6];
            Marshal.Copy(data + 560, bssidBytes, 0, 6);

            return (state, profile, Encoding.ASCII.GetString(ssidBytes), FormatMac(bssidBytes));
        }
        catch
        {
            return null; // the query is best-effort - never break adapter listing
        }
        finally
        {
            if (data != IntPtr.Zero) WlanFreeMemory(data);
        }
    }

    // ===================== scan =====================

    private List<WifiNetwork> ScanCore(IProgress<string>? log, CancellationToken ct)
    {
        IntPtr h = OpenHandle();
        try
        {
            var ifaces = EnumInterfaces(h);
            if (ifaces.Count == 0)
            {
                log?.Report("No Wi-Fi interfaces present on this machine.");
                return new List<WifiNetwork>();
            }

            foreach (var ifc in ifaces)
            {
                ct.ThrowIfCancellationRequested();
                var g = ifc.InterfaceGuid;
                uint rc = WlanScan(h, ref g, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                log?.Report($"WlanScan '{ifc.InterfaceDescription}' -> rc={rc}");
            }

            // scans populate the OS lists asynchronously - give them a moment
            for (int waited = 0; waited < 3500; waited += 500)
            {
                ct.ThrowIfCancellationRequested();
                Thread.Sleep(500);
            }

            var merged = new Dictionary<string, WifiNetwork>(StringComparer.Ordinal);
            foreach (var ifc in ifaces)
            {
                ct.ThrowIfCancellationRequested();
                var g = ifc.InterfaceGuid;
                var bss = ReadBssMap(h, g);
                log?.Report($"Interface '{ifc.InterfaceDescription}': {bss.Count} BSS entr(ies) decoded.");

                foreach (var n in ReadAvailableNetworks(h, g))
                {
                    string bssid = "";
                    uint freq = 0;
                    if (bss.TryGetValue(n.Ssid, out var found)) { bssid = found.Bssid; freq = found.Freq; }
                    var (channel, band) = freq > 0 ? ChannelFromFreq(freq) : (0, "");
                    var candidate = new WifiNetwork(n.Ssid, bssid, channel, band, n.Signal, n.Auth, n.Encryption, n.Connected);
                    if (!merged.TryGetValue(n.Ssid, out var existing) || candidate.SignalPercent > existing.SignalPercent)
                        merged[n.Ssid] = candidate;
                }
            }

            var list = merged.Values.OrderByDescending(n => n.SignalPercent).ToList();
            log?.Report($"Merged {list.Count} unique SSID(s) (hidden networks show as <hidden>).");
            return list;
        }
        finally { WlanCloseHandle(h, IntPtr.Zero); }
    }

    private List<(string Ssid, int Signal, string Auth, string Encryption, bool Connected)> ReadAvailableNetworks(IntPtr h, Guid g)
    {
        var list = new List<(string Ssid, int Signal, string Auth, string Encryption, bool Connected)>();
        uint rc = WlanGetAvailableNetworkList(h, ref g, 0x3 /* all adhoc profiles + hidden */, IntPtr.Zero, out IntPtr ptr);
        if (rc != 0 || ptr == IntPtr.Zero) return list;
        try
        {
            var header = Marshal.PtrToStructure<WLAN_AVAILABLE_NETWORK_LIST>(ptr);
            int stride = Marshal.SizeOf<WLAN_AVAILABLE_NETWORK>();
            for (int i = 0; i < header.dwNumberOfItems; i++)
            {
                var e = Marshal.PtrToStructure<WLAN_AVAILABLE_NETWORK>(header.Network + i * stride);
                bool connected = (e.dwFlags & 0x1) != 0; // WLAN_AVAILABLE_NETWORK_CONNECTED
                list.Add((SsidString(e.dot11Ssid), (int)e.wlanSignalQuality, AuthName(e.dot11AuthAlgo), CipherName(e.dot11CipherAlgo), connected));
            }
            return list;
        }
        finally { WlanFreeMemory(ptr); }
    }

    /// <summary>
    /// Parses WlanGetNetworkBssList into SSID -> (strongest BSSID, RSSI, frequency).
    /// The native BSS list stores variable-length entries; the walk below validates
    /// each header (SSID length, RSSI range, link quality, BSS type, channel band,
    /// non-zero BSSID) before trusting it, and stops at the end of the buffer, so a
    /// driver reporting an unexpected layout can only ever degrade the enrichment
    /// (BSSID/channel) - never the authoritative data from the available-network list.
    /// </summary>
    private Dictionary<string, (string Bssid, int Rssi, uint Freq)> ReadBssMap(IntPtr h, Guid g)
    {
        var map = new Dictionary<string, (string Bssid, int Rssi, uint Freq)>(StringComparer.Ordinal);
        uint rc = WlanGetNetworkBssList(h, ref g, IntPtr.Zero, 3 /* any */, false, IntPtr.Zero, out IntPtr ptr);
        if (rc != 0 || ptr == IntPtr.Zero) return map;
        try
        {
            var list = Marshal.PtrToStructure<WLAN_BSS_LIST>(ptr);
            int n = list.dwNumberOfItems > int.MaxValue ? int.MaxValue : (int)list.dwNumberOfItems;
            if (n <= 0 || list.wlanBssEntries == IntPtr.Zero) return map;

            int headerSize = Marshal.SizeOf<WLAN_BSS_ENTRY>(); // 360 on x64
            long total = list.dwTotalSize >= headerSize && list.dwTotalSize < (1 << 24) ? list.dwTotalSize : (long)headerSize * n;

            int offset = 0;
            for (int i = 0; i < n; i++)
            {
                if (offset + headerSize > total) break;
                var e = Marshal.PtrToStructure<WLAN_BSS_ENTRY>(list.wlanBssEntries + offset);

                if (IsValidBss(e))
                {
                    string key = SsidString(e.dot11Ssid);
                    if (!map.TryGetValue(key, out var best) || e.lRssi > best.Rssi)
                        map[key] = (FormatMac(e.dot11Bssid), e.lRssi, e.ulChCenterFrequency);
                }

                // advance past the fixed part plus this entry's inline IE blob
                int advance = e.ulIeOffset > 0 && e.ulIeOffset <= (1 << 16) ? (int)e.ulIeOffset : headerSize;
                advance += e.ulIeSize <= (1 << 16) ? (int)e.ulIeSize : 0;
                if (advance < headerSize) break;
                offset += advance;
            }
            return map;
        }
        catch
        {
            return new Dictionary<string, (string Bssid, int Rssi, uint Freq)>(); // enrichment is optional
        }
        finally { WlanFreeMemory(ptr); }
    }

    private static bool IsValidBss(WLAN_BSS_ENTRY e)
    {
        if (e.dot11Ssid.uSSIDLength > 32) return false;
        if (e.lRssi < -100 || e.lRssi > 20) return false;
        if (e.uLinkQuality > 100) return false;
        if (e.dot11BssType is < 1 or > 3) return false;
        if (e.ulChCenterFrequency != 0 && ChannelFromFreq(e.ulChCenterFrequency).Channel == 0) return false;
        if (e.dot11Bssid == null || e.dot11Bssid.Length != 6 || e.dot11Bssid.All(b => b == 0)) return false;
        return true;
    }

    // ===================== connect / disconnect =====================

    private async Task<bool> ConnectCoreAsync(WifiConnectRequest req, CancellationToken ct)
    {
        IntPtr h = OpenHandle();
        try
        {
            var ifaces = EnumInterfaces(h);
            if (ifaces.Count == 0)
                throw new InvalidOperationException("No Wi-Fi interface available to connect with.");
            var g = ifaces[0].InterfaceGuid;

            string xml = BuildProfileXml(req.Ssid, req.Password, req.Auth);
            uint rc = WlanSetProfile(h, ref g, 0 /* all-user profile */, xml, null, true, IntPtr.Zero, out uint reason);
            if (rc != 0)
                throw new InvalidOperationException($"WlanSetProfile failed (Win32 error {rc}, reason 0x{reason:X}).");

            var p = new WLAN_CONNECTION_PARAMETERS
            {
                wlanConnectionMode = 0, // wlan_connection_mode_profile
                strProfile = req.Ssid,  // profile <name> == SSID
                dot11BssType = 3,       // any
                dwFlags = 0,
                pDot11Ssid = IntPtr.Zero
            };
            rc = WlanConnect(h, ref g, ref p, IntPtr.Zero);
            Throw(rc, "WlanConnect");

            // poll the real interface state for up to 15 seconds
            for (int elapsed = 0; elapsed < 15000; elapsed += 500)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(500, ct);
                var conn = QueryConnection(h, g);
                if (conn.HasValue && conn.Value.State == 1) return true; // connected
            }
            return false;
        }
        finally { WlanCloseHandle(h, IntPtr.Zero); }
    }

    private void DisconnectCore()
    {
        IntPtr h = OpenHandle();
        try
        {
            foreach (var ifc in EnumInterfaces(h))
            {
                var g = ifc.InterfaceGuid;
                uint rc = WlanDisconnect(h, ref g, IntPtr.Zero);
                if (rc == 0 && ifc.isState == 1)
                {
                    // wait briefly for the real state to settle
                    for (int waited = 0; waited < 5000; waited += 250)
                    {
                        Thread.Sleep(250);
                        var conn = QueryConnection(h, g);
                        if (!conn.HasValue || conn.Value.State == 2) break; // disconnected
                    }
                }
            }
        }
        finally { WlanCloseHandle(h, IntPtr.Zero); }
    }

    /// <summary>Builds a real WLANProfile XML document (Native Wifi profile schema).</summary>
    internal static string BuildProfileXml(string ssid, string password, string auth)
    {
        string s = SecurityElement.Escape(ssid) ?? ssid;
        bool hasPsk = !string.IsNullOrEmpty(password);
        string authentication;
        string encryption;
        string? keyXml = null;

        switch ((auth ?? "").Trim().ToUpperInvariant())
        {
            case "WPA3":
                authentication = "WPA3SAE";
                encryption = "AES";
                if (hasPsk) keyXml = KeyXml("passPhrase", password);
                break;
            case "WPA2":
            case "WPA2-PSK":
                authentication = "WPA2PSK";
                encryption = "AES";
                if (hasPsk) keyXml = KeyXml("passPhrase", password);
                break;
            case "WPA":
            case "WPA-PSK":
                authentication = "WPAPSK";
                encryption = "TKIP";
                if (hasPsk) keyXml = KeyXml("passPhrase", password);
                break;
            case "WEP":
                authentication = "open";
                encryption = "WEP";
                if (hasPsk) keyXml = KeyXml("networkKey", password);
                break;
            default: // Open / unknown -> open network profile
                authentication = "open";
                encryption = "none";
                break;
        }

        return $@"<?xml version=""1.0"" encoding=""utf-8""?>
<WLANProfile xmlns=""http://www.microsoft.com/networking/WLAN/profile/v1"">
  <name>{s}</name>
  <SSIDConfig>
    <SSID><name>{s}</name></SSID>
  </SSIDConfig>
  <connectionType>ESS</connectionType>
  <connectionMode>manual</connectionMode>
  <MSM>
    <security>
      <authEncryption>
        <authentication>{authentication}</authentication>
        <encryption>{encryption}</encryption>
        <useOneX>false</useOneX>
      </authEncryption>
      {(keyXml ?? "")}
    </security>
  </MSM>
</WLANProfile>";
    }

    private static string KeyXml(string keyType, string key)
        => $"<sharedKey><keyType>{keyType}</keyType><protected>false</protected><keyMaterial>{SecurityElement.Escape(key)}</keyMaterial></sharedKey>";

    // ===================== profiles =====================

    private List<(string Name, string Auth)> GetProfilesCore()
    {
        IntPtr h = OpenHandle();
        try
        {
            var ifaces = EnumInterfaces(h);
            var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ifc in ifaces)
            {
                var g = ifc.InterfaceGuid;
                uint rc = WlanGetProfileList(h, ref g, IntPtr.Zero, out IntPtr listPtr);
                if (rc != 0 || listPtr == IntPtr.Zero) continue;
                try
                {
                    var list = Marshal.PtrToStructure<WLAN_PROFILE_INFO_LIST>(listPtr);
                    int itemSize = Marshal.SizeOf<WLAN_PROFILE_INFO>(); // 256 WCHAR + DWORD = 516
                    for (int i = 0; i < list.dwNumberOfItems; i++)
                    {
                        var info = Marshal.PtrToStructure<WLAN_PROFILE_INFO>(list.ProfileInfo + i * itemSize);
                        if (string.IsNullOrEmpty(info.strProfileName) || byName.ContainsKey(info.strProfileName)) continue;
                        byName[info.strProfileName] = FetchProfileAuth(h, g, info.strProfileName);
                    }
                }
                finally { WlanFreeMemory(listPtr); }
            }
            return byName.Select(kv => (kv.Key, kv.Value)).ToList();
        }
        finally { WlanCloseHandle(h, IntPtr.Zero); }
    }

    private static string FetchProfileAuth(IntPtr h, Guid g, string profileName)
    {
        try
        {
            uint rc = WlanGetProfile(h, ref g, profileName, IntPtr.Zero, out IntPtr xmlPtr, IntPtr.Zero, out _);
            if (rc != 0 || xmlPtr == IntPtr.Zero) return "Unknown";
            try
            {
                string xml = Marshal.PtrToStringUni(xmlPtr) ?? "";
                return ParseAuthFromXml(xml);
            }
            finally { WlanFreeMemory(xmlPtr); }
        }
        catch { return "Unknown"; }
    }

    private static string ParseAuthFromXml(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            XNamespace ns = "http://www.microsoft.com/networking/WLAN/profile/v1";
            string auth = doc.Descendants(ns + "authentication").FirstOrDefault()?.Value ?? "";
            string enc = doc.Descendants(ns + "encryption").FirstOrDefault()?.Value ?? "";
            return auth switch
            {
                "WPA3SAE" => "WPA3",
                "WPA2PSK" or "WPA2" => "WPA2",
                "WPAPSK" or "WPA" => "WPA",
                "shared" or "WEP" => "WEP",
                "open" => string.Equals(enc, "none", StringComparison.OrdinalIgnoreCase) ? "Open" : "WEP",
                "" => "Unknown",
                _ => auth
            };
        }
        catch { return "Unknown"; }
    }

    private bool DeleteProfileCore(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        IntPtr h = OpenHandle();
        try
        {
            bool any = false;
            foreach (var ifc in EnumInterfaces(h))
            {
                var g = ifc.InterfaceGuid;
                if (WlanDeleteProfile(h, ref g, name, IntPtr.Zero) == 0) any = true;
            }
            return any;
        }
        finally { WlanCloseHandle(h, IntPtr.Zero); }
    }

    // ===================== helpers =====================

    private static string SsidString(DOT11_SSID s)
    {
        int len = s.uSSIDLength > 32 ? 32 : (int)s.uSSIDLength;
        if (len <= 0 || s.ucSSID == null) return "";
        var b = new byte[len];
        Array.Copy(s.ucSSID, b, len);
        return Encoding.ASCII.GetString(b);
    }

    internal static (int Channel, string Band) ChannelFromFreq(uint freqMHz)
    {
        if (freqMHz >= 5950 && freqMHz <= 7115) return ((int)((freqMHz - 5950) / 5), "6 GHz");
        if (freqMHz >= 5000 && freqMHz <= 5900) return ((int)((freqMHz - 5000) / 5), "5 GHz");
        if (freqMHz == 2484) return (14, "2.4 GHz");
        if (freqMHz >= 2412 && freqMHz <= 2472) return ((int)((freqMHz - 2407) / 5), "2.4 GHz");
        return (0, "");
    }

    internal static string AuthName(uint algo) => algo switch
    {
        1 => "Open",        // DOT11_AUTH_ALGO_80211_OPEN
        2 => "WEP",         // DOT11_AUTH_ALGO_80211_SHARED_KEY
        3 => "WPA",         // DOT11_AUTH_ALGO_WPA
        4 => "WPA",         // DOT11_AUTH_ALGO_WPA_PSK
        5 => "WPA2",        // DOT11_AUTH_ALGO_RSNA
        6 => "WPA2",        // DOT11_AUTH_ALGO_RSNA_PSK
        7 => "WPA",         // DOT11_AUTH_ALGO_WPA_NONE
        0x10 => "WPA3",     // DOT11_AUTH_ALGO_RSNA_SAE
        0xFF => "Unknown",  // IHV range
        _ => "Unknown"
    };

    internal static string CipherName(uint algo) => algo switch
    {
        0x00 => "NONE",      // DOT11_CIPHER_ALGO_NONE
        0x01 => "WEP40",     // DOT11_CIPHER_ALGO_WEP40
        0x02 => "TKIP",      // DOT11_CIPHER_ALGO_TKIP
        0x04 => "AES",       // DOT11_CIPHER_ALGO_CCMP
        0x05 => "WEP104",    // DOT11_CIPHER_ALGO_WEP104
        0x06 => "GCMP",      // DOT11_CIPHER_ALGO_GCMP
        0x07 => "GCMP-256",  // DOT11_CIPHER_ALGO_GCMP_256
        0x08 => "CCMP-256",  // DOT11_CIPHER_ALGO_CCMP_256
        0xFF => "Unknown",   // IHV range
        _ => "Unknown"
    };

    internal static string FormatMac(byte[] bytes)
        => bytes == null || bytes.Length == 0 ? "" : string.Join(":", bytes.Select(b => b.ToString("X2")));
}
