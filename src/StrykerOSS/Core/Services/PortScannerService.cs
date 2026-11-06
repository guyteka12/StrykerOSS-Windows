using System.Net.Sockets;
using System.Text;
using StrykerOSS.Core.Services;

namespace StrykerOSS.Core.Services;

/// <summary>
/// Real TCP connect() port scanner with banner grabbing.
/// Every result comes from an actual socket connection attempt on the target host.
/// </summary>
public sealed class PortScannerService : IPortScanner
{
    /// <summary>Ports whose services typically send a greeting banner right after connect.</summary>
    private static readonly int[] BannerPorts =
        { 21, 22, 25, 79, 80, 110, 143, 587, 993, 995, 3306, 5432, 5900, 6379, 8080, 8443 };

    private const int BannerReadTimeoutMs = 1500;

    public IReadOnlyList<int> CommonPorts { get; } = new[]
    {
        21, 22, 23, 25, 53, 80, 110, 111, 135, 139, 143, 443, 445, 993, 995,
        1433, 1723, 2375, 3306, 3389, 5432, 5601, 5900, 5984, 6379, 8080,
        8443, 8888, 9200, 27017, 32768
    };

    public async Task<List<PortResult>> ScanTcpAsync(string host, IEnumerable<int> ports, int timeoutMs,
        int maxConcurrency, IProgress<PortResult>? prog = null, CancellationToken ct = default)
    {
        var results = new List<PortResult>();
        if (string.IsNullOrWhiteSpace(host) || ports == null) return results;

        var portList = ports.Distinct().Where(p => p is >= 1 and <= 65535).OrderBy(p => p).ToList();
        if (portList.Count == 0) return results;
        if (timeoutMs <= 0) timeoutMs = 800;

        var gate = new SemaphoreSlim(Math.Clamp(maxConcurrency, 1, 500));
        var lockObj = new object();
        var tasks = new List<Task>(portList.Count);

        try
        {
            foreach (var port in portList)
            {
                ct.ThrowIfCancellationRequested();
                await gate.WaitAsync(ct).ConfigureAwait(false);
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var res = await ProbePortAsync(host, port, timeoutMs, ct).ConfigureAwait(false);
                        if (res != null)
                        {
                            lock (lockObj) results.Add(res);
                            prog?.Report(res);
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { /* unreachable host - keep scanning */ }
                    finally { gate.Release(); }
                }, ct));
            }
        }
        catch (OperationCanceledException) { /* stop scheduling new probes */ }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        lock (lockObj)
        {
            return results.OrderBy(r => r.Port).ToList();
        }
    }

    public int[] ParsePorts(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return Array.Empty<int>();

        var set = new SortedSet<int>();
        foreach (var raw in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (set.Count >= 65535) break;

            if (raw.Contains('-'))
            {
                var range = raw.Split('-', 2);
                if (int.TryParse(range[0], out var lo) && int.TryParse(range[1], out var hi))
                {
                    lo = Math.Clamp(lo, 1, 65535);
                    hi = Math.Clamp(hi, 1, 65535);
                    if (lo > hi) (lo, hi) = (hi, lo);
                    for (int p = lo; p <= hi && set.Count < 65535; p++) set.Add(p);
                }
            }
            else if (int.TryParse(raw, out var single) && single is >= 1 and <= 65535)
            {
                set.Add(single);
            }
        }
        return set.ToArray();
    }

    // ---------------- internals ----------------

    private static async Task<PortResult?> ProbePortAsync(string host, int port, int timeoutMs, CancellationToken ct)
    {
        bool open = false;
        string? banner = null;
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync(host, port, ct).AsTask();
            var timeout = Task.Delay(timeoutMs, ct);
            var done = await Task.WhenAny(connect, timeout).ConfigureAwait(false);

            if (done != connect)
            {
                ObserveLostTask(connect);
                return new PortResult(host, port, false, null);
            }

            try { await connect.ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { return new PortResult(host, port, false, null); }

            open = true;
            if (port != 443 && BannerPorts.Contains(port)) // raw read never decodes TLS on 443 - skip
                banner = await ReadBannerAsync(client, ct).ConfigureAwait(false);

            return new PortResult(host, port, true, string.IsNullOrWhiteSpace(banner) ? null : banner);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return new PortResult(host, port, open, banner);
        }
    }

    private static async Task<string?> ReadBannerAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            var stream = client.GetStream();
            if (!stream.CanRead) return null;

            var buf = new byte[256];
            var read = stream.ReadAsync(buf, ct).AsTask();
            var done = await Task.WhenAny(read, Task.Delay(BannerReadTimeoutMs, ct)).ConfigureAwait(false);
            if (done != read)
            {
                ObserveLostTask(read);
                return null;
            }

            int n = await read.ConfigureAwait(false);
            if (n <= 0) return null;

            var sb = new StringBuilder();
            for (int i = 0; i < n && sb.Length < 200; i++)
            {
                char c = (char)buf[i];
                if (c == '\r' || c == '\n' || c == '\t') sb.Append(c);
                else if (c >= 32 && c < 127) sb.Append(c);
            }
            var text = sb.ToString().Trim();
            return text.Length == 0 ? null : text;
        }
        catch { return null; }
    }

    /// <summary>Detaches an abandoned task and makes sure its exception is observed.</summary>
    private static void ObserveLostTask(Task task)
    {
        _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
    }
}
