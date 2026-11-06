using System.Collections.Generic;

namespace StrykerOSS.Core;

// ============================================================
// StrykerOSS - Core contracts (Windows port of zalexdev/strykerapp)
// Every service is REAL: native Windows APIs, real processes, real data.
// ============================================================

#region Settings & Infrastructure

public sealed class AppSettings
{
    public bool ConsentAccepted { get; set; }
    public DateTime? ConsentDate { get; set; }
    public string DataDir { get; set; } = "";
    public string ToolsDir { get; set; } = "";
    public string? NmapPath { get; set; }
    public string? NucleiPath { get; set; }
    public string? MsfPath { get; set; }
    public string MsfRpcHost { get; set; } = "127.0.0.1";
    public int MsfRpcPort { get; set; } = 55553;
    public string MsfRpcUser { get; set; } = "msf";
    public string MsfRpcPass { get; set; } = "";
    public string DefaultAuthor { get; set; } = "StrykerOSS Operator";
}

public record LogEntry(int Id, DateTime Time, string Level, string Source, string Message);

public record NavItem(string Key, string Label, string Glyph);

public record DashboardStats(int Projects, int Scans, int Findings, int CriticalFindings,
    int Reports, int Sessions, int Handshakes, int MacChanges, int Templates, int GeoPoints);

public interface IAdmin
{
    bool IsElevated { get; }
    string WindowsVersion();
    string MachineName();
    string CurrentUser();
}

public interface ISettings
{
    AppSettings Value { get; }
    void Save();
    string EnsureDataDir();
}

public interface ILogger
{
    event Action<LogEntry>? Logged;
    void Info(string source, string message);
    void Warn(string source, string message);
    void Error(string source, string message, Exception? ex = null);
    IReadOnlyList<LogEntry> Recent(int count);
}

public interface INavigation
{
    string CurrentKey { get; }
    void GoTo(string key);
    event Action<string>? Navigated;
}

public interface IToolLocator
{
    string? Find(params string[] exeNames);
    string? ResolveTool(string key); // nmap | nuclei | msfconsole | msfvenom | hashcat
    string ToolsDir();
}

#endregion

#region Wi-Fi (Native Wifi API - built-in adapter, no external hardware)

public record WifiAdapterInfo(string Name, string Description, string Guid, string State,
    bool Connected, string? ConnectedSsid, string? ConnectedBssid);

public record WifiNetwork(string Ssid, string Bssid, int Channel, string Band,
    int SignalPercent, string Auth, string Encryption, bool Connected);

public record WifiConnectRequest(string Ssid, string Password, string Auth);

public interface IWifiService
{
    Task<List<WifiAdapterInfo>> GetAdaptersAsync();
    Task<List<WifiNetwork>> ScanAsync(IProgress<string>? log = null, CancellationToken ct = default);
    Task<bool> ConnectAsync(WifiConnectRequest req, CancellationToken ct = default);
    Task DisconnectAsync();
    Task<List<(string Name, string Auth)>> GetProfilesAsync();
    Task<bool> DeleteProfileAsync(string name);
    Task<WifiAdapterInfo?> GetCurrentConnectionAsync();
}

#endregion

#region MAC Changer (registry-based, real, requires admin = we are admin)

public record MacChangerAdapter(string Name, string InterfaceGuid, string Description,
    string CurrentMac, string PermanentMac, bool Wireless);

public record MacProfile(int Id, string Name, string InterfaceGuid, string Mac, DateTime Created);

public interface IMacChanger
{
    Task<List<MacChangerAdapter>> ListAdaptersAsync();
    Task<bool> SetMacAsync(string interfaceGuid, string mac, IProgress<string>? log = null, CancellationToken ct = default);
    Task RestorePermanentAsync(string interfaceGuid, IProgress<string>? log = null);
    string RandomMac();
    Task<List<MacProfile>> GetProfilesAsync();
    Task SaveProfileAsync(string name, string interfaceGuid, string mac);
    Task DeleteProfileAsync(int id);
}

#endregion

#region Handshakes (real pcap/EAPOL analysis + hashcat 22000 export)

public record HandshakeInfo(int Id, string FilePath, string? Ssid, string? Bssid,
    int EapolFrames, bool HasHandshake, List<string> ClientMacs, DateTime Analyzed);

public interface IHandshakeService
{
    Task<HandshakeInfo> AnalyzeAsync(string pcapPath, CancellationToken ct = default);
    Task<List<HandshakeInfo>> AnalyzeDirectoryAsync(string dir, IProgress<string>? log = null, CancellationToken ct = default);
    Task<string> ExportHashcat22000Async(HandshakeInfo h, string outDir);
    Task<string> ExportCsvAsync(IEnumerable<HandshakeInfo> list, string outPath);
}

#endregion

#region HID Attacks (real DuckyScript parser + real SendInput execution on this machine)

public record HidScriptInfo(string Name, int Lines, int StringChars, TimeSpan Estimated);

public interface IHidService
{
    Task<HidScriptInfo> ParseAsync(string duckPath);
    Task RunAsync(string duckPath, IProgress<string>? log = null, CancellationToken ct = default);
    Task StopAsync();
    bool IsRunning { get; }
}

#endregion

#region Local Network (ICMP + ARP + DNS - all real) & Port Scanner (real TCP connect)

public record LanDevice(string Ip, string? Hostname, string? Mac, string? Vendor,
    bool IsGateway, int LatencyMs, List<int> OpenPorts);

public record PortResult(string Ip, int Port, bool Open, string? Banner);

public interface ILanService
{
    Task<(string Gateway, string Cidr)> DetectNetworkAsync();
    Task<List<LanDevice>> DiscoverAsync(IProgress<string>? log = null, CancellationToken ct = default);
    Task<LanDevice?> ProbeAsync(string ip);
}

public interface IPortScanner
{
    Task<List<PortResult>> ScanTcpAsync(string host, IEnumerable<int> ports, int timeoutMs,
        int maxConcurrency, IProgress<PortResult>? prog = null, CancellationToken ct = default);
    IReadOnlyList<int> CommonPorts { get; }
    int[] ParsePorts(string spec);
}

#endregion

#region Nmap (real nmap.exe wrapper + real XML parsing + report export)

public record NmapProfile(string Key, string Name, string Args, string Description);

public record NmapPort(int Port, string Proto, string State, string Service, string? Version, string? ScriptOutput);

public record NmapHost(string Ip, string? Hostname, string? OsGuess, string? Mac, string? MacVendor,
    bool Up, List<NmapPort> Ports);

public record NmapRun(string Target, DateTime Started, TimeSpan Duration, List<NmapHost> Hosts, string? RawXmlPath);

public interface INmapService
{
    string? FindNmap();
    IReadOnlyList<NmapProfile> Profiles { get; }
    Task<NmapRun> RunAsync(string target, string args, IProgress<string>? log = null,
        CancellationToken ct = default, string? profileName = null);
    Task<string> ExportHtmlAsync(NmapRun run, string outDir);
}

#endregion

#region Nuclei Web Scanner (real nuclei binary, real JSON findings)

public record NucleiFinding(string TemplateId, string Name, string Severity, string Host,
    string MatchedAt, string? Extracted, string? Description, string? Reference, DateTime FoundAt);

public record NucleiRun(string Targets, DateTime Started, TimeSpan Duration,
    List<NucleiFinding> Findings, string? RawPath);

public interface INucleiService
{
    string? FindNuclei();
    Task<string> EnsureInstalledAsync(IProgress<string>? log = null, CancellationToken ct = default);
    Task<NucleiRun> ScanAsync(IEnumerable<string> targets, IEnumerable<string>? severities,
        IEnumerable<string>? tags, IProgress<string>? log = null, CancellationToken ct = default);
}

#endregion

#region Exploit Database (real offline SearchSploit-style CSV database)

public record ExploitEntry(string Id, string File, string Description, string Date,
    string Author, string Type, string Platform, string Port);

public interface IExploitDbService
{
    Task EnsureDbAsync(IProgress<string>? log = null, CancellationToken ct = default);
    Task<List<ExploitEntry>> SearchAsync(string q, string? type, string? platform, int limit = 300, CancellationToken ct = default);
    Task<string?> FetchEntryFileAsync(ExploitEntry e, IProgress<string>? log = null, CancellationToken ct = default);
    int Total { get; }
    DateTime? UpdatedAt { get; }
}

#endregion

#region Arsenal (custom scanner/exploit command templates, real execution)

public record ArsenalTemplate(int Id, string Name, string Category, string Command, string Description);

public interface IArsenalService
{
    Task<List<ArsenalTemplate>> ListAsync();
    Task<int> SaveAsync(ArsenalTemplate t);
    Task DeleteAsync(int id);
    string Render(ArsenalTemplate t, string ip, string? port, string? mac, string? gw, string? mask);
    Task<string> ExecuteAsync(ArsenalTemplate t, string ip, string? port, string? mac,
        string? gw, string? mask, IProgress<string>? log = null, CancellationToken ct = default);
    Task SeedIfEmptyAsync();
}

#endregion

#region WhisperPair BLE (real Bluetooth LE watcher + Fast Pair detection)

public record BleDevice(string Address, string? Name, short Rssi, bool IsFastPair,
    string? ModelId, string? Company, DateTime FirstSeen, DateTime LastSeen, string? CveNote);

public interface IBleService
{
    bool IsSupported { get; }
    Task StartAsync(IProgress<string>? log = null);
    Task StopAsync();
    event Action<BleDevice>? Found;
    IReadOnlyList<BleDevice> Snapshot();
    Task<string> EnumerateGattAsync(string address, IProgress<string>? log = null, CancellationToken ct = default);
}

#endregion

#region GeoMac (real IP geolocation + KML/CSV WiGLE-style export)

public record GeoPoint(int Id, string Kind, string Label, string? Bssid, string? Mac,
    double? Lat, double? Lon, DateTime Added);

public record GeoIpInfo(string Query, string Country, string RegionName, string City,
    string Isp, string Org, string As, double Lat, double Lon, string Timezone);

public interface IGeoMacService
{
    Task<GeoIpInfo?> LookupIpAsync(string ip, CancellationToken ct = default);
    Task<string?> PublicIpAsync();
    Task<string> ExportKmlAsync(IEnumerable<GeoPoint> pts, string path);
    Task<string> ExportCsvAsync(IEnumerable<GeoPoint> pts, string path);
    Task<List<GeoPoint>> ImportWiGleCsvAsync(string path, IProgress<string>? log = null);
}

#endregion

#region Metasploit (real detection + real MSGRPC client + payload generation)

public record MsfSession(int Id, string Type, string TunnelPeer, string Username,
    string TargetHost, string Platform, DateTime? OpenedAt);

public record MsfModule(string FullName, string Name, string Rank, string DisclosureDate,
    string Platform, string? Author, string? Description, string Type);

public interface IMetasploit
{
    Task<bool> DetectAsync();
    string? MsfPath { get; }
    bool IsConnected { get; }
    Task<bool> ConnectRpcAsync(IProgress<string>? log = null, CancellationToken ct = default);
    Task DisconnectAsync();
    Task<List<MsfSession>> ListSessionsAsync();
    Task<bool> KillSessionAsync(int id);
    Task<List<MsfModule>> SearchModulesAsync(string term, int limit = 50);
    Task<List<MsfModule>> RecentExploitsAsync(int limit = 25);
    Task<string> GeneratePayloadAsync(string payload, string lhost, int lport, string format,
        string outFile, IProgress<string>? log = null, CancellationToken ct = default);
    Task<string> RunModuleAsync(string module, Dictionary<string, string> opts,
        IProgress<string>? log = null, CancellationToken ct = default);
    event Action? SessionsChanged;
}

#endregion

#region Terminal (real elevated PowerShell/cmd process with redirected streams)

public interface ITerminalService
{
    Task<ITerminalSession> StartAsync(string shell, string? cwd, IProgress<string>? output,
        CancellationToken ct = default);
}

public interface ITerminalSession : IAsyncDisposable
{
    void WriteLine(string line);
    bool IsAlive { get; }
    event Action? Exited;
    int? Pid { get; }
}

#endregion

#region USB Arsenal (real USB enumeration + real ISO mount/eject)

public record UsbDeviceInfo(string PnpId, string? Name, string? Manufacturer, string Class,
    bool IsStorage, string? DriveLetter, long? SizeBytes);

public interface IUsbService
{
    Task<List<UsbDeviceInfo>> ListUsbDevicesAsync();
    Task<bool> MountIsoAsync(string isoPath, IProgress<string>? log = null);
    Task<bool> EjectDriveAsync(string driveLetter, IProgress<string>? log = null);
}

#endregion

#region Tools Manager (Core manager equivalent - real install/verify)

public record ToolStatus(string Key, string Name, string? Path, bool Found, string? Version, string InstallHint);

public interface IToolsManager
{
    Task<List<ToolStatus>> StatusAsync();
    Task<bool> InstallAsync(string key, IProgress<string>? log = null, CancellationToken ct = default);
    string ToolsDir();
}

#endregion

#region Database / Projects / Findings / Notes / Scans / Reports / Sessions

public record Project(int Id, string Name, string? Description, string Client, string Status,
    DateTime Created, DateTime Updated);

public record Finding(int Id, int ProjectId, string Title, string Severity, string? Description,
    string? Impact, string? Poc, string? Remediation, DateTime Created);

public record NoteItem(int Id, int ProjectId, string Title, string Body, DateTime Created);

public record ScanRecord(int Id, int? ProjectId, string Tool, string Target, DateTime Started,
    int DurationMs, string Summary, string? RawPath);

public record ReportRecord(int Id, int? ProjectId, string Title, string Format, string Path,
    DateTime Created, string Author);

public record SessionRecord(int Id, string Name, string Kind, string Target, DateTime Started,
    DateTime? Ended, string? Notes);

public interface IDbService
{
    Task InitAsync();

    Task<List<Project>> GetProjectsAsync();
    Task<Project> CreateProjectAsync(string name, string? description, string client);
    Task UpdateProjectAsync(Project p);
    Task DeleteProjectAsync(int id);

    Task<List<Finding>> GetFindingsAsync(int projectId);
    Task<Finding> AddFindingAsync(Finding f);
    Task UpdateFindingAsync(Finding f);
    Task DeleteFindingAsync(int id);

    Task<List<NoteItem>> GetNotesAsync(int projectId);
    Task<NoteItem> AddNoteAsync(NoteItem n);
    Task UpdateNoteAsync(NoteItem n);
    Task DeleteNoteAsync(int id);

    Task AddScanAsync(ScanRecord r);
    Task<List<ScanRecord>> GetScansAsync(int? projectId = null, int limit = 200);

    Task AddReportAsync(ReportRecord r);
    Task<List<ReportRecord>> GetReportsAsync(int? projectId = null);

    Task<SessionRecord> StartSessionAsync(string name, string kind, string target);
    Task EndSessionAsync(int id);
    Task<List<SessionRecord>> GetSessionsAsync(int limit = 100);

    Task AddLogDbAsync(LogEntry e);
    Task<List<LogEntry>> GetLogsAsync(int limit = 500);

    Task<List<ArsenalTemplate>> GetTemplatesAsync();
    Task<int> SaveTemplateAsync(ArsenalTemplate t);
    Task DeleteTemplateAsync(int id);

    Task<List<MacProfile>> GetMacProfilesAsync();
    Task<int> SaveMacProfileAsync(MacProfile p);
    Task DeleteMacProfileAsync(int id);

    Task<int> AddHandshakeAsync(HandshakeInfo h);
    Task<List<HandshakeInfo>> GetHandshakesAsync();
    Task DeleteHandshakeAsync(int id);

    Task<int> AddGeoPointAsync(GeoPoint p);
    Task<List<GeoPoint>> GetGeoPointsAsync();
    Task DeleteGeoPointAsync(int id);

    Task<DashboardStats> GetStatsAsync();
    Task<string> BackupProjectAsync(int projectId, string outDir);
}

#endregion

#region Reports (real PDF via QuestPDF + HTML + Markdown)

public interface IReportService
{
    Task<string> GenerateAsync(Project? project, IList<Finding> findings, string title,
        string author, string? execSummary, string fmt, string outDir, CancellationToken ct = default);
}

#endregion

// OUI vendor lookup model used by Lan/Nmap/GeoMac views
public record MacVendorInfo(string Mac, string Oui, string Vendor);

public interface IOuiService
{
    Task InitializeAsync();
    MacVendorInfo? Lookup(string mac);
    int EntryCount { get; }
}
