using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using MessagePack;
using MessagePack.Resolvers;

namespace StrykerOSS.Core.Services;

/// <summary>
/// Real Metasploit integration: msfconsole/msfvenom detection, real MSGRPC (MessagePack over HTTP)
/// client against msfrpcd and real msfvenom payload generation.
/// </summary>
public sealed class MetasploitService : IMetasploit
{
    private readonly IToolLocator _tools;
    private readonly ISettings _settings;
    private readonly ILogger _log;
    private readonly HttpClient _http;
    private string? _msfvenomPath;
    private int _msgid;
    private string? _token;

    public string? MsfPath { get; private set; }
    public bool IsConnected { get; private set; }

    public event Action? SessionsChanged;

    public MetasploitService(IToolLocator tools, ISettings settings, ILogger log)
    {
        _tools = tools;
        _settings = settings;
        _log = log;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    }

    public Task<bool> DetectAsync()
    {
        MsfPath = _tools.ResolveTool("msfconsole");
        _msfvenomPath = _tools.ResolveTool("msfvenom"); // checked separately: msfvenom can exist without msfconsole
        _log.Info("msf", $"detect: msfconsole={(MsfPath ?? "not found")} msfvenom={(_msfvenomPath ?? "not found")}");
        return Task.FromResult(MsfPath != null);
    }

    // ============ MSGRPC ============

    public async Task<bool> ConnectRpcAsync(IProgress<string>? log = null, CancellationToken ct = default)
    {
        try
        {
            var host = _settings.Value.MsfRpcHost;
            var port = _settings.Value.MsfRpcPort;
            var user = _settings.Value.MsfRpcUser;
            var pass = _settings.Value.MsfRpcPass;
            log?.Report($"Connecting MSGRPC {host}:{port} as {user} ...");

            var result = await RpcCoreAsync("auth.login", ct, new object[] { user, pass });
            var token = result switch
            {
                string s => s,
                IDictionary<object, object> d => GetStr(d, "token"),
                _ => null
            };
            if (string.IsNullOrEmpty(token)) throw new Exception("auth.login returned no token");

            _token = token;
            IsConnected = true;
            log?.Report("MSGRPC connected.");
            _log.Info("msf", $"MSGRPC connected to {host}:{port}");
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _token = null;
            IsConnected = false;
            log?.Report($"MSGRPC connect failed: {ex.Message}");
            _log.Error("msf", "MSGRPC connect failed", ex);
            return false;
        }
    }

    public Task DisconnectAsync()
    {
        _token = null;
        IsConnected = false;
        _log.Info("msf", "MSGRPC disconnected");
        return Task.CompletedTask;
    }

    public async Task<List<MsfSession>> ListSessionsAsync()
    {
        EnsureConnected();
        var result = await RpcAsync("session.list");
        var list = new List<MsfSession>();
        if (result is IDictionary<object, object> map)
        {
            foreach (var kv in map)
            {
                int id = Convert.ToInt32(kv.Key, CultureInfo.InvariantCulture);
                if (kv.Value is not IDictionary<object, object> info) continue;
                list.Add(new MsfSession(
                    id,
                    GetStr(info, "type") ?? "",
                    GetStr(info, "tunnel_peer") ?? "",
                    GetStr(info, "username") ?? "",
                    GetStr(info, "target_host") ?? "",
                    GetStr(info, "platform") ?? "",
                    ParseOpenedAt(GetStr(info, "opened_at"))));
            }
        }
        SessionsChanged?.Invoke();
        return list;
    }

    public async Task<bool> KillSessionAsync(int id)
    {
        EnsureConnected();
        var result = await RpcAsync("session.kill", id);
        var ok = result switch
        {
            bool b => b,
            string s => s == "success",
            IDictionary<object, object> d => GetStr(d, "result") == "success",
            _ => result != null
        };
        if (ok) SessionsChanged?.Invoke();
        return ok;
    }

    public async Task<List<MsfModule>> SearchModulesAsync(string term, int limit = 50)
    {
        EnsureConnected();
        var result = await RpcAsync("module.search", term);
        var list = new List<MsfModule>();
        if (result is object[] rows)
        {
            foreach (var row in rows)
            {
                if (row is not IDictionary<object, object> d) continue;
                var fullname = GetStr(d, "fullname") ?? "";
                if (fullname.Length == 0) continue;
                var type = GetStr(d, "type");
                if (string.IsNullOrEmpty(type)) type = TypeFromFullName(fullname);
                list.Add(new MsfModule(
                    fullname,
                    GetStr(d, "name") ?? fullname,
                    GetStr(d, "rank") ?? "",
                    GetStr(d, "disclosure_date") ?? "",
                    Flatten(d.TryGetValue("platform", out var plat) ? plat : null),
                    NullIfEmpty(Flatten(d.TryGetValue("author", out var author) ? author : null)),
                    GetStr(d, "description"),
                    type));
                if (list.Count >= limit) break;
            }
        }
        return list;
    }

    public async Task<List<MsfModule>> RecentExploitsAsync(int limit = 25)
    {
        EnsureConnected();
        var result = await RpcAsync("module.exploits");
        var list = new List<MsfModule>();
        if (result is object[] rows)
        {
            foreach (var row in rows)
            {
                if (row is not string name || name.Length == 0) continue;
                var segments = name.Split('/');
                list.Add(new MsfModule(name, segments.Last(), "manual", "", "", null, null, "exploit"));
                if (list.Count >= limit) break;
            }
        }
        return list;
    }

    public async Task<string> RunModuleAsync(string module, Dictionary<string, string> opts,
        IProgress<string>? log = null, CancellationToken ct = default)
    {
        EnsureConnected();
        var created = await RpcAsync("console.create");
        string cid = created is IDictionary<object, object> cc && cc.TryGetValue("id", out var idObj)
            ? idObj?.ToString() ?? ""
            : "";
        if (string.IsNullOrEmpty(cid)) throw new Exception("console.create failed - no console id returned");
        log?.Report($"msfrpcd console {cid} created for {module}");
        try
        {
            var script = new StringBuilder();
            script.Append("use ").AppendLine(module);
            foreach (var kv in opts)
                script.Append("set ").Append(kv.Key).Append(' ').Append(kv.Value).AppendLine();
            script.AppendLine("run");
            await RpcAsync("console.write", cid, script.ToString());

            var collected = new StringBuilder();
            int consecutiveEmpty = 0;
            for (int i = 0; i < 60; i++)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(1000, ct);
                var read = await RpcAsync("console.read", cid);
                var data = read is IDictionary<object, object> rd && rd.TryGetValue("data", out var dv)
                    ? dv is byte[] bytes ? Encoding.UTF8.GetString(bytes) : dv?.ToString() ?? ""
                    : "";
                if (data.Length > 0)
                {
                    collected.Append(data);
                    consecutiveEmpty = 0;
                }
                else if (++consecutiveEmpty >= 3)
                {
                    break;
                }
            }
            log?.Report($"console read finished ({collected.Length} chars)");
            return collected.ToString();
        }
        finally
        {
            try { await RpcAsync("console.destroy", cid); } catch { /* best effort cleanup */ }
        }
    }

    // ============ msfvenom ============

    public async Task<string> GeneratePayloadAsync(string payload, string lhost, int lport, string format,
        string outFile, IProgress<string>? log = null, CancellationToken ct = default)
    {
        var msfvenom = _msfvenomPath ?? _tools.ResolveTool("msfvenom")
            ?? throw new InvalidOperationException("msfvenom not found - install Metasploit Framework (see Tools Manager).");

        var full = Path.GetFullPath(outFile);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var psi = new ProcessStartInfo
        {
            FileName = msfvenom,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(payload);
        psi.ArgumentList.Add($"LHOST={lhost}");
        psi.ArgumentList.Add($"LPORT={lport.ToString(CultureInfo.InvariantCulture)}");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add(format);
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(full);

        log?.Report($"Running: msfvenom -p {payload} LHOST={lhost} LPORT={lport} -f {format}");
        using var pr = Process.Start(psi) ?? throw new Exception("failed to start msfvenom process");
        var errTail = new StringBuilder();
        pr.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            log?.Report(e.Data);
        };
        pr.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            errTail.AppendLine(e.Data);
            log?.Report("[msfvenom] " + e.Data);
        };
        pr.BeginOutputReadLine();
        pr.BeginErrorReadLine();
        using var reg = ct.Register(() => { try { if (!pr.HasExited) pr.Kill(true); } catch { } });
        await pr.WaitForExitAsync(ct);

        if (File.Exists(full))
        {
            log?.Report($"Payload written: {full}");
            _log.Info("msf", $"payload generated: {full}");
            return full;
        }
        var tail = errTail.ToString();
        if (tail.Length > 800) tail = tail[^800..];
        throw new Exception($"msfvenom failed (exit {pr.ExitCode}): {tail.Trim()}");
    }

    // ---- MSGRPC plumbing (real MessagePack msgpack-rpc protocol) ----

    private Task<object?> RpcAsync(string method, params object[] args)
        => RpcCoreAsync(method, CancellationToken.None, args);

    private async Task<object?> RpcCoreAsync(string method, CancellationToken ct, object[] args)
    {
        if (method != "auth.login" && _token == null)
            throw new InvalidOperationException("Not connected to msfrpcd - connect first.");

        var call = new List<object>();
        if (method != "auth.login") call.Add(_token!); // msf convention: token first arg (except auth.login)
        call.AddRange(args);

        int msgid = Interlocked.Increment(ref _msgid);
        var body = MessagePackSerializer.Serialize(
            (object)new object[] { 0, msgid, method, call.ToArray() },
            PrimitiveObjectResolver.Options);

        var url = $"http://{_settings.Value.MsfRpcHost}:{_settings.Value.MsfRpcPort}/api/1.1/";
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/msgpack");
        using var resp = await _http.PostAsync(url, content, ct);
        resp.EnsureSuccessStatusCode();
        var respBytes = await resp.Content.ReadAsByteArrayAsync(ct);
        var obj = MessagePackSerializer.Deserialize<object>(respBytes, PrimitiveObjectResolver.Options);

        if (obj is not object[] arr || arr.Length < 4)
            throw new Exception("Malformed MSGRPC response frame.");
        if (Convert.ToInt64(arr[1], CultureInfo.InvariantCulture) != msgid)
            throw new Exception("MSGRPC response msgid mismatch.");
        if (arr[2] != null)
            throw new Exception(arr[2].ToString() ?? "MSGRPC error");
        return arr[3];
    }

    private void EnsureConnected()
    {
        if (!IsConnected || _token == null)
            throw new InvalidOperationException("Not connected to msfrpcd - connect first (start msfrpcd -P pass -S false).");
    }

    // ---- helpers ----

    private static string? GetStr(IDictionary<object, object> d, string key)
    {
        if (!d.TryGetValue(key, out var v) || v == null) return null;
        if (v is byte[] b) return Encoding.UTF8.GetString(b);
        return v.ToString();
    }

    private static string Flatten(object? o)
    {
        if (o == null) return "";
        if (o is object[] arr) return string.Join(", ", arr.Select(Flatten).Where(s => s.Length > 0));
        if (o is byte[] b) return Encoding.UTF8.GetString(b);
        return o.ToString() ?? "";
    }

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

    private static string TypeFromFullName(string fullname)
    {
        if (fullname.StartsWith("exploit/", StringComparison.OrdinalIgnoreCase)) return "exploit";
        if (fullname.StartsWith("auxiliary/", StringComparison.OrdinalIgnoreCase)) return "auxiliary";
        if (fullname.StartsWith("post/", StringComparison.OrdinalIgnoreCase)) return "post";
        if (fullname.StartsWith("payload/", StringComparison.OrdinalIgnoreCase)) return "payload";
        return "unknown";
    }

    /// <summary>Parses real msfrpcd timestamps like "2015-07-12 05:31:42 -0500"; missing/unparsable → null.</summary>
    private static DateTime? ParseOpenedAt(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
            return dto.LocalDateTime;
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return dt;
        return null;
    }
}
