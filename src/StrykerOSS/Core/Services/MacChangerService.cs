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
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Le nom du profil est requis.", nameof(name));
        if (string.IsNullOrWhiteSpace(interfaceGuid)) throw new ArgumentException("Le GUID d'interface est requis.", nameof(interfaceGuid));
        string normalized = NormalizeMac(mac);
        if (normalized.Length != 12) throw new ArgumentException("La MAC doit comporter 12 caractères hexadécimaux.", nameof(mac));
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
            throw new ArgumentException("Le GUID d'interface est requis.", nameof(interfaceGuid));
        if (!Guid.TryParse(interfaceGuid, out _))
            throw new ArgumentException($"'{interfaceGuid}' n'est pas un GUID d'interface valide.", nameof(interfaceGuid));

        string clean = NormalizeMac(mac);
        if (clean.Length != 12 || clean.Any(c => !Uri.IsHexDigit(c)))
            throw new ArgumentException("La MAC doit comporter exactement 12 caractères hexadécimaux (0-9, A-F).", nameof(mac));

        string guid = interfaceGuid.Trim('{', '}');
        string? subKey = FindClassSubKey(guid)
            ?? throw new InvalidOperationException($"Aucune clé de classe de pilote trouvée pour l'interface {guid}.");

        log?.Report($"Écriture de HKLM\\{ClassKeyPath}\\{subKey}\\NetworkAddress = {FormatHexMac(clean)}");
        using (var key = Registry.LocalMachine.OpenSubKey($@"{ClassKeyPath}\{subKey}", writable: true))
        {
            if (key == null)
                throw new UnauthorizedAccessException(
                    "Impossible d'ouvrir la clé de registre de l'adaptateur en écriture (droits administrateur requis).");
            key.SetValue("NetworkAddress", clean, RegistryValueKind.String);
        }
        log?.Report("Surcharge de registre écrite (REG_SZ).");

        string connectionName = GetConnectionName(guid);
        bool restarted = false;
        if (!string.IsNullOrWhiteSpace(connectionName))
        {
            log?.Report($"Redémarrage de l'adaptateur '{connectionName}' via netsh (le pilote relit la MAC)...");
            RunNetsh($"interface set interface name=\"{connectionName}\" admin=disabled", log);
            await Task.Delay(1500, ct);
            RunNetsh($"interface set interface name=\"{connectionName}\" admin=enabled", log);
            restarted = true;
        }
        else
        {
            log?.Report("Aucun NetConnectionID trouvé - désactivez/activez l'adaptateur manuellement (ou redémarrez) pour appliquer.");
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
                log?.Report($"La MAC effective est désormais {current} (confirmée par l'OS).");
                return true;
            }
        }

        log?.Report(restarted
            ? "Registre écrit et adaptateur redémarré, mais la nouvelle MAC n'a pas été confirmée en 10 s (certains pilotes appliquent au prochain redémarrage)."
            : "Registre écrit, mais l'adaptateur n'a pas été redémarré - le changement s'appliquera au prochain cycle désactivation/activation.");
        return restarted;
    }

    private async Task RestoreCoreAsync(string interfaceGuid, IProgress<string>? log, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(interfaceGuid))
            throw new ArgumentException("Le GUID d'interface est requis.", nameof(interfaceGuid));

        string guid = interfaceGuid.Trim('{', '}');
        string? subKey = FindClassSubKey(guid)
            ?? throw new InvalidOperationException($"Aucune clé de classe de pilote trouvée pour l'interface {guid}.");

        bool deleted = false;
        using (var key = Registry.LocalMachine.OpenSubKey($@"{ClassKeyPath}\{subKey}", writable: true))
        {
            if (key == null)
                throw new UnauthorizedAccessException(
                    "Impossible d'ouvrir la clé de registre de l'adaptateur en écriture (droits administrateur requis).");
            if (key.GetValueNames().Any(n => string.Equals(n, "NetworkAddress", StringComparison.OrdinalIgnoreCase)))
            {
                key.DeleteValue("NetworkAddress", throwOnMissingValue: false);
                deleted = true;
            }
        }

        log?.Report(deleted
            ? "Surcharge NetworkAddress supprimée - l'adaptateur réutilisera sa MAC matérielle."
            : "Aucune surcharge NetworkAddress présente - rien à restaurer.");

        string connectionName = GetConnectionName(guid);
        if (!string.IsNullOrWhiteSpace(connectionName))
        {
            log?.Report($"Redémarrage de l'adaptateur '{connectionName}' via netsh...");
            RunNetsh($"interface set interface name=\"{connectionName}\" admin=disabled", log);
            await Task.Delay(1500, ct);
            RunNetsh($"interface set interface name=\"{connectionName}\" admin=enabled", log);
        }
        else
        {
            log?.Report("Aucun NetConnectionID trouvé - désactivez/activez l'adaptateur manuellement (ou redémarrez) pour appliquer.");
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
