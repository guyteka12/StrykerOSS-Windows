using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace StrykerOSS.Core.Services;

// ============================================================
// MacChangerService - REAL MAC address override for Windows adapters.
//
// How it really works (the same technique used by Technitium and tools alike):
//  1. Per-adapter driver settings live under
//     HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}\<NNNN>
//     A "NetworkAddress" REG_SZ value overrides the hardware (burned-in) MAC
//     when the NDIS driver initialises.
//  2. The adapter is bounced with `netsh interface set interface admin=disabled`
//     -> `admin=enabled` so the driver re-reads the value.
//  3. The effective MAC is verified against NetworkInterface.GetAllNetworkInterfaces()
//     (live kernel data) for up to 10 seconds.
//
// The burned-in MAC cannot be read back without NDIS/WMI, so "PermanentMac"
// carries the registry-configured address: the live MAC while no override is
// present, or the stored override value itself when one is active (the UI
// shows an OVERRIDE badge by comparing the two).
// ============================================================
public sealed class MacChangerService : IMacChanger
{
    private const string ClassKeyPath =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";

    private const string NetworkKeyRoot =
        @"SYSTEM\CurrentControlSet\Control\Network\{4d36e972-e325-11ce-bfc1-08002be10318}";

    private static readonly byte[] LocallyAdministeredFirstOctets = { 0x02, 0x06, 0x0A, 0x0E };

    private readonly IDbService _db;

    public MacChangerService(IDbService db) => _db = db;

    // ===================== IMacChanger =====================

    public Task<List<MacChangerAdapter>> ListAdaptersAsync() => Task.Run(ListAdaptersCore);

    public Task<bool> SetMacAsync(string interfaceGuid, string mac, IProgress<string>? log = null, CancellationToken ct = default)
        => Task.Run(() => SetMacCoreAsync(interfaceGuid, mac, log, ct), ct);

    public Task RestorePermanentAsync(string interfaceGuid, IProgress<string>? log = null)
        => Task.Run(() => RestoreCoreAsync(interfaceGuid, log, CancellationToken.None));

    public string RandomMac()
    {
        var bytes = RandomNumberGenerator.GetBytes(6);
        bytes[0] = LocallyAdministeredFirstOctets[RandomNumberGenerator.GetInt32(0, LocallyAdministeredFirstOctets.Length)];
        return string.Join(":", bytes.Select(b => b.ToString("X2")));
    }

    public Task<List<MacProfile>> GetProfilesAsync() => _db.GetMacProfilesAsync();

    public async Task SaveProfileAsync(string name, string interfaceGuid, string mac)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Profile name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(interfaceGuid)) throw new ArgumentException("Interface GUID is required.", nameof(interfaceGuid));
        string normalized = NormalizeMac(mac);
        if (normalized.Length != 12) throw new ArgumentException("MAC must be 12 hex characters.", nameof(mac));
        await _db.SaveMacProfileAsync(new MacProfile(0, name.Trim(), interfaceGuid, normalized, DateTime.Now));
    }

    public Task DeleteProfileAsync(int id) => _db.DeleteMacProfileAsync(id);

    // ===================== listing =====================

    private List<MacChangerAdapter> ListAdaptersCore()
    {
        var result = new List<MacChangerAdapter>();
        using var classKey = Registry.LocalMachine.OpenSubKey(ClassKeyPath);
        if (classKey == null) return result;

        NetworkInterface[] nics;
        try { nics = NetworkInterface.GetAllNetworkInterfaces(); }
        catch { nics = Array.Empty<NetworkInterface>(); }

        foreach (var sub in classKey.GetSubKeyNames())
        {
            if (sub.Length != 4 || !sub.All(char.IsDigit)) continue; // ^[0-9]{4}$
            using var key = classKey.OpenSubKey(sub);
            if (key == null) continue;

            var description = key.GetValue("DriverDesc") as string;
            var netCfgInstanceId = key.GetValue("NetCfgInstanceId") as string;
            if (string.IsNullOrWhiteSpace(description) || string.IsNullOrWhiteSpace(netCfgInstanceId)) continue;

            string guid = netCfgInstanceId.Trim('{', '}');
            var nic = FindNic(guid);
            string currentMac = nic == null ? "" : FormatMac(nic.GetPhysicalAddress().GetAddressBytes());
            bool wireless = nic?.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;

            string connectionName = GetConnectionName(guid);
            var overrideMac = key.GetValue("NetworkAddress") as string;
            // "PermanentMac" = configured address: live MAC normally, stored override when active
            string configured = string.IsNullOrWhiteSpace(overrideMac) ? currentMac : overrideMac.Trim();

            result.Add(new MacChangerAdapter(
                string.IsNullOrWhiteSpace(connectionName) ? description : connectionName,
                guid,
                description,
                currentMac,
                configured,
                wireless));
        }
        return result;
    }

    private static string GetConnectionName(string guid)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"{NetworkKeyRoot}\{{{guid}}}\Connection");
            return key?.GetValue("Name") as string ?? "";
        }
        catch { return ""; }
    }

    private static string? FindClassSubKey(string guid)
    {
        using var classKey = Registry.LocalMachine.OpenSubKey(ClassKeyPath);
        if (classKey == null) return null;
        foreach (var sub in classKey.GetSubKeyNames())
        {
            if (sub.Length != 4 || !sub.All(char.IsDigit)) continue;
            using var key = classKey.OpenSubKey(sub);
            var value = key?.GetValue("NetCfgInstanceId") as string;
            if (value != null && value.Trim('{', '}').Equals(guid, StringComparison.OrdinalIgnoreCase))
                return sub;
        }
        return null;
    }

    private static NetworkInterface? FindNic(string guid)
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.Id.Replace("{", "").Replace("}", "")
                    .Equals(guid, StringComparison.OrdinalIgnoreCase));
        }
        catch { return null; }
    }

    // ===================== set / restore =====================

    private async Task<bool> SetMacCoreAsync(string interfaceGuid, string mac, IProgress<string>? log, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(interfaceGuid))
            throw new ArgumentException("Interface GUID is required.", nameof(interfaceGuid));
        if (!Guid.TryParse(interfaceGuid, out _))
            throw new ArgumentException($"'{interfaceGuid}' is not a valid interface GUID.", nameof(interfaceGuid));

        string clean = NormalizeMac(mac);
        if (clean.Length != 12 || clean.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("MAC must be exactly 12 hex characters (0-9, A-F).", nameof(mac));

        string guid = interfaceGuid.Trim('{', '}');
        string? subKey = FindClassSubKey(guid)
            ?? throw new InvalidOperationException($"No driver class key found for interface {guid}.");

        log?.Report($"Writing HKLM\\{ClassKeyPath}\\{subKey}\\NetworkAddress = {FormatHexMac(clean)}");
        using (var key = Registry.LocalMachine.OpenSubKey($@"{ClassKeyPath}\{subKey}", writable: true))
        {
            if (key == null)
                throw new UnauthorizedAccessException(
                    "Cannot open the adapter registry key for writing (Administrator rights are required).");
            key.SetValue("NetworkAddress", clean, RegistryValueKind.String);
        }
        log?.Report("Registry override written (REG_SZ).");

        string connectionName = GetConnectionName(guid);
        bool restarted = false;
        if (!string.IsNullOrWhiteSpace(connectionName))
        {
            log?.Report($"Restarting adapter '{connectionName}' via netsh (driver re-reads the MAC)...");
            RunNetsh($"interface set interface name=\"{connectionName}\" admin=disabled", log);
            await Task.Delay(1500, ct);
            RunNetsh($"interface set interface name=\"{connectionName}\" admin=enabled", log);
            restarted = true;
        }
        else
        {
            log?.Report("No NetConnectionID found - disable/enable the adapter manually (or reboot) to apply.");
        }

        // poll live kernel data for the new effective MAC
        for (int waited = 0; waited < 10000; waited += 500)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(500, ct);
            var nic = FindNic(guid);
            if (nic == null) continue;
            string current = FormatMac(nic.GetPhysicalAddress().GetAddressBytes());
            if (current.Replace(":", "").Equals(clean, StringComparison.OrdinalIgnoreCase))
            {
                log?.Report($"Effective MAC is now {current} (confirmed by the OS).");
                return true;
            }
        }

        log?.Report(restarted
            ? "Registry written and adapter restarted, but the new MAC was not confirmed within 10 s (some drivers apply on the next reboot)."
            : "Registry written, but the adapter was not restarted - the change applies on the next disable/enable cycle.");
        return restarted;
    }

    private async Task RestoreCoreAsync(string interfaceGuid, IProgress<string>? log, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(interfaceGuid))
            throw new ArgumentException("Interface GUID is required.", nameof(interfaceGuid));

        string guid = interfaceGuid.Trim('{', '}');
        string? subKey = FindClassSubKey(guid)
            ?? throw new InvalidOperationException($"No driver class key found for interface {guid}.");

        bool deleted = false;
        using (var key = Registry.LocalMachine.OpenSubKey($@"{ClassKeyPath}\{subKey}", writable: true))
        {
            if (key == null)
                throw new UnauthorizedAccessException(
                    "Cannot open the adapter registry key for writing (Administrator rights are required).");
            if (key.GetValueNames().Any(n => string.Equals(n, "NetworkAddress", StringComparison.OrdinalIgnoreCase)))
            {
                key.DeleteValue("NetworkAddress", throwOnMissingValue: false);
                deleted = true;
            }
        }

        log?.Report(deleted
            ? "Deleted NetworkAddress override - the adapter will use its hardware MAC again."
            : "No NetworkAddress override present - nothing to restore.");

        string connectionName = GetConnectionName(guid);
        if (!string.IsNullOrWhiteSpace(connectionName))
        {
            log?.Report($"Restarting adapter '{connectionName}' via netsh...");
            RunNetsh($"interface set interface name=\"{connectionName}\" admin=disabled", log);
            await Task.Delay(1500, ct);
            RunNetsh($"interface set interface name=\"{connectionName}\" admin=enabled", log);
        }
        else
        {
            log?.Report("No NetConnectionID found - disable/enable the adapter manually (or reboot) to apply.");
        }
    }

    // ===================== helpers =====================

    private static string RunNetsh(string args, IProgress<string>? log)
    {
        using var p = new Process();
        p.StartInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "netsh.exe"),
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        p.Start();
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        if (!p.WaitForExit(15000))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
        }
        string output = (stdout + " " + stderr).Trim();
        if (output.Length > 0) log?.Report($"netsh: {output}");
        return output;
    }

    private static string NormalizeMac(string s)
        => (s ?? "").Replace(":", "").Replace("-", "").Trim().ToUpperInvariant();

    private static string FormatHexMac(string hex12)
        => hex12.Length == 12
            ? string.Join(":", Enumerable.Range(0, 6).Select(i => hex12.Substring(i * 2, 2)))
            : hex12;

    private static string FormatMac(byte[] bytes)
        => bytes == null || bytes.Length == 0 ? "" : string.Join(":", bytes.Select(b => b.ToString("X2")));
}
