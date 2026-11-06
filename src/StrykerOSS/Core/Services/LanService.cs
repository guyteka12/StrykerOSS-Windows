using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using StrykerOSS.Core.Services;

namespace StrykerOSS.Core.Services;

/// <summary>
/// Real local network discovery: ICMP ping sweep + SendARP (iphlpapi) MAC resolution
/// + reverse DNS + OUI vendor lookup. No simulation - every host is probed for real.
/// </summary>
public sealed class LanService : ILanService
{
    private const int PingTimeoutMs = 500;
    private const int DnsTimeoutMs = 300;
    private const int MaxConcurrency = 64;
    private const int MaxHosts = 4096;

    private readonly IOuiService _oui;

    public LanService(IOuiService oui) => _oui = oui;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint SendARP(uint destIp, uint srcIp, out ulong macAddr, out uint phyAddrLen);

    public Task<(string Gateway, string Cidr)> DetectNetworkAsync() => Task.Run(() =>
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType != NetworkInterfaceType.Ethernet &&
                    nic.NetworkInterfaceType != NetworkInterfaceType.Wireless80211) continue;

                var props = nic.GetIPProperties();
                var gw4 = props.GatewayAddresses
                    .Select(g => g.Address)
                    .FirstOrDefault(a => a != null && a.AddressFamily == AddressFamily.InterNetwork);
                if (gw4 == null) continue;

                var uai = props.UnicastAddresses.FirstOrDefault(u =>
                    u.Address != null && u.Address.AddressFamily == AddressFamily.InterNetwork);
                if (uai == null) continue;

                var prefix = uai.PrefixLength;
                if (prefix < 0 || prefix > 32) prefix = MaskToPrefix(uai.IPv4Mask);
                if (prefix < 0 || prefix > 32) prefix = 24;

                return (gw4.ToString(), $"{uai.Address}/{prefix}");
            }
            catch { continue; }
        }
        return ("", "");
    });

    public async Task<List<LanDevice>> DiscoverAsync(IProgress<string>? log = null, CancellationToken ct = default)
    {
        var (gateway, cidr) = await DetectNetworkAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(cidr))
        {
            log?.Report("No active Ethernet/Wi-Fi adapter with a default gateway detected.");
            return new List<LanDevice>();
        }

        var ips = ExpandCidr(cidr);
        if (ips.Count == 0)
        {
            log?.Report($"Could not expand network {cidr}.");
            return new List<LanDevice>();
        }

        await _oui.InitializeAsync().ConfigureAwait(false);

        var total = ips.Count;
        log?.Report($"Scanning {total} hosts on {cidr} (gateway {gateway}) - ICMP {PingTimeoutMs}ms timeout, {MaxConcurrency} parallel...");

        var found = new ConcurrentBag<LanDevice>();
        int done = 0, alive = 0;
        using var gate = new SemaphoreSlim(MaxConcurrency);
        const int chunkSize = MaxConcurrency;

        for (int offset = 0; offset < total; offset += chunkSize)
        {
            ct.ThrowIfCancellationRequested();

            var batch = new List<Task>(chunkSize);
            var gwLocal = gateway;
            for (int i = offset; i < Math.Min(offset + chunkSize, total); i++)
            {
                var ip = ips[i];
                batch.Add(Task.Run(async () =>
                {
                    await gate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        var dev = await ProbeInternalAsync(ip, gwLocal, ct).ConfigureAwait(false);
                        if (dev != null)
                        {
                            found.Add(dev);
                            Interlocked.Increment(ref alive);
                        }
                        Interlocked.Increment(ref done);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { /* a single dead host must never kill the sweep */ }
                    finally { gate.Release(); }
                }, ct));
            }

            await Task.WhenAll(batch).ConfigureAwait(false);
            log?.Report($"scanned {done}/{total}, found {alive}");
        }

        ct.ThrowIfCancellationRequested();
        return found.OrderBy(d => IpToNumber(d.Ip)).ToList();
    }

    public async Task<LanDevice?> ProbeAsync(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return null;
        var clean = ip.Trim();
        if (!IPAddress.TryParse(clean, out _)) return null;

        var (gateway, _) = await DetectNetworkAsync().ConfigureAwait(false);
        await _oui.InitializeAsync().ConfigureAwait(false);
        return await ProbeInternalAsync(clean, gateway, CancellationToken.None).ConfigureAwait(false);
    }

    // ---------------- internals ----------------

    private async Task<LanDevice?> ProbeInternalAsync(string ip, string gateway, CancellationToken ct)
    {
        int latencyMs;
        using (var ping = new Ping())
        {
            PingReply reply;
            try { reply = await ping.SendPingAsync(ip, PingTimeoutMs).ConfigureAwait(false); }
            catch { return null; }
            if (reply.Status != IPStatus.Success) return null;
            latencyMs = (int)reply.RoundtripTime;
        }

        ct.ThrowIfCancellationRequested();
        var mac = QueryArp(ip);

        ct.ThrowIfCancellationRequested();
        var hostname = await ResolveHostnameAsync(ip, ct).ConfigureAwait(false);

        string? vendor = null;
        if (mac != null)
        {
            try { vendor = _oui.Lookup(mac)?.Vendor; } catch { }
        }

        bool isGateway;
        try { isGateway = string.Equals(IPAddress.Parse(ip).ToString(), IPAddress.Parse(gateway).ToString(), StringComparison.OrdinalIgnoreCase); }
        catch { isGateway = string.Equals(ip, gateway, StringComparison.OrdinalIgnoreCase); }

        return new LanDevice(ip, hostname, mac, vendor, isGateway, latencyMs, new List<int>());
    }

    private static string? QueryArp(string ip)
    {
        try
        {
            var dest = BitConverter.ToUInt32(IPAddress.Parse(ip).GetAddressBytes(), 0);
            uint rc = SendARP(dest, 0, out ulong macBuf, out uint phyAddrLen);
            if (rc != 0 || phyAddrLen != 6) return null;

            var b = BitConverter.GetBytes(macBuf);
            return $"{b[0]:X2}:{b[1]:X2}:{b[2]:X2}:{b[3]:X2}:{b[4]:X2}:{b[5]:X2}";
        }
        catch { return null; }
    }

    private static async Task<string?> ResolveHostnameAsync(string ip, CancellationToken ct)
    {
        try
        {
            var dns = Dns.GetHostEntryAsync(ip);
            var finished = await Task.WhenAny(dns, Task.Delay(DnsTimeoutMs, ct)).ConfigureAwait(false);
            if (finished == dns)
            {
                try { return (await dns.ConfigureAwait(false)).HostName; }
                catch { return null; }
            }

            // DNS too slow - detach and observe its eventual exception
            _ = dns.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            ct.ThrowIfCancellationRequested();
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    /// <summary>Expands a.b.c.d/prefix into concrete IPv4 strings (/16../32, capped at MaxHosts).</summary>
    private static List<string> ExpandCidr(string cidr)
    {
        var result = new List<string>();
        var parts = cidr.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var baseIp)) return result;
        if (!int.TryParse(parts[1], out var prefix)) return result;

        prefix = Math.Clamp(prefix, 16, 32);
        var baseNum = BitConverter.ToUInt32(baseIp.GetAddressBytes(), 0);
        uint total = 1u << (32 - prefix);

        // skip network+broadcast for normal prefixes
        uint start = prefix <= 30 ? 1u : 0u;
        uint end = prefix <= 30 ? total - 1u : total;

        for (uint i = start; i < end && result.Count < MaxHosts; i++)
        {
            var bytes = BitConverter.GetBytes(baseNum + i);
            result.Add(new IPAddress(bytes).ToString());
        }
        return result;
    }

    private static uint IpToNumber(string ip)
    {
        try { return BitConverter.ToUInt32(IPAddress.Parse(ip).GetAddressBytes(), 0); }
        catch { return 0xFFFFFFFFu; }
    }

    private static int MaskToPrefix(IPAddress? mask)
    {
        if (mask == null) return 24;
        var bits = 0;
        foreach (var b in mask.GetAddressBytes())
        {
            int v = b;
            while ((v & 0x80) != 0) { bits++; v <<= 1; }
            if (v != 0) break; // non-contiguous mask - stop
        }
        return bits is > 0 and <= 32 ? bits : 24;
    }
}
