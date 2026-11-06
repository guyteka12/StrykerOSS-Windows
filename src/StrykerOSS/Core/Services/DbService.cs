using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace StrykerOSS.Core.Services;

/// <summary>
/// Real SQLite persistence for StrykerOSS (Microsoft.Data.Sqlite, WAL mode, shared cache).
/// One fresh connection per operation; schema is created idempotently on first use.
/// Dates are stored as ISO 8601 round-trip strings ("o").
/// </summary>
public sealed class DbService : IDbService
{
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly ILogger _log;
    private readonly string _cs;
    private bool _initialized;

    public DbService(ISettings settings, ILogger log)
    {
        _log = log;
        var dataDir = settings.EnsureDataDir();
        var dbPath = Path.Combine(dataDir, "stryker.db");
        _cs = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
    }

    // ---------------- infrastructure ----------------

    public async Task InitAsync() => await EnsureInitAsync();

    private async Task<SqliteConnection> OpenAsync()
    {
        await EnsureInitAsync();
        var con = new SqliteConnection(_cs);
        await con.OpenAsync();
        using (var busy = con.CreateCommand())
        {
            busy.CommandText = "PRAGMA busy_timeout=5000;";
            busy.ExecuteNonQuery();
        }
        return con;
    }

    private async Task EnsureInitAsync()
    {
        if (_initialized) return;
        await _initLock.WaitAsync();
        try
        {
            if (_initialized) return;
            await InitCoreAsync();
            _initialized = true;
        }
        finally { _initLock.Release(); }
    }

    private async Task InitCoreAsync()
    {
        using var con = new SqliteConnection(_cs);
        await con.OpenAsync();

        using (var pragma = con.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL;";
            await pragma.ExecuteNonQueryAsync();
        }

        const string ddl = """
            CREATE TABLE IF NOT EXISTS projects(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, description TEXT, client TEXT, status TEXT DEFAULT 'Active', created TEXT, updated TEXT);
            CREATE TABLE IF NOT EXISTS findings(id INTEGER PRIMARY KEY AUTOINCREMENT, project_id INTEGER, title TEXT, severity TEXT, description TEXT, impact TEXT, poc TEXT, remediation TEXT, created TEXT);
            CREATE TABLE IF NOT EXISTS notes(id INTEGER PRIMARY KEY AUTOINCREMENT, project_id INTEGER, title TEXT, body TEXT, created TEXT);
            CREATE TABLE IF NOT EXISTS scans(id INTEGER PRIMARY KEY AUTOINCREMENT, project_id INTEGER, tool TEXT, target TEXT, started TEXT, duration_ms INTEGER, summary TEXT, raw_path TEXT);
            CREATE TABLE IF NOT EXISTS reports(id INTEGER PRIMARY KEY AUTOINCREMENT, project_id INTEGER, title TEXT, format TEXT, path TEXT, created TEXT, author TEXT);
            CREATE TABLE IF NOT EXISTS sessions(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT, kind TEXT, target TEXT, started TEXT, ended TEXT, notes TEXT);
            CREATE TABLE IF NOT EXISTS logs(id INTEGER PRIMARY KEY AUTOINCREMENT, time TEXT, level TEXT, source TEXT, message TEXT);
            CREATE TABLE IF NOT EXISTS arsenal(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT, category TEXT, command TEXT, description TEXT);
            CREATE TABLE IF NOT EXISTS mac_profiles(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT, interface_guid TEXT, mac TEXT, created TEXT);
            CREATE TABLE IF NOT EXISTS handshakes(id INTEGER PRIMARY KEY AUTOINCREMENT, file_path TEXT, ssid TEXT, bssid TEXT, eapol_frames INTEGER, has_handshake INTEGER, clients TEXT, analyzed TEXT);
            CREATE TABLE IF NOT EXISTS geomac(id INTEGER PRIMARY KEY AUTOINCREMENT, kind TEXT, label TEXT, bssid TEXT, mac TEXT, lat REAL, lon REAL, added TEXT);
            """;
        using (var ddlCmd = con.CreateCommand())
        {
            ddlCmd.CommandText = ddl;
            await ddlCmd.ExecuteNonQueryAsync();
        }
        _log.Info("db", "SQLite schema ready (WAL): " + _cs);
    }

    // ---------------- low-level helpers ----------------

    private static SqliteCommand Cmd(SqliteConnection con, string sql)
    {
        var c = con.CreateCommand();
        c.CommandText = sql;
        return c;
    }

    private static void AddParams(SqliteCommand cmd, params (string Name, object? Value)[] args)
    {
        foreach (var (n, v) in args)
            cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
    }

    private async Task<int> ExecAsync(string sql, params (string Name, object? Value)[] args)
    {
        using var con = await OpenAsync();
        using var cmd = Cmd(con, sql);
        AddParams(cmd, args);
        return await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> InsertAsync(string sql, params (string Name, object? Value)[] args)
    {
        using var con = await OpenAsync();
        using var cmd = Cmd(con, sql + "; SELECT last_insert_rowid();");
        AddParams(cmd, args);
        var o = await cmd.ExecuteScalarAsync();
        return Convert.ToInt64(o ?? 0L);
    }

    private async Task<List<T>> QueryAsync<T>(string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] args)
    {
        using var con = await OpenAsync();
        using var cmd = Cmd(con, sql);
        AddParams(cmd, args);
        using var r = await cmd.ExecuteReaderAsync();
        var list = new List<T>();
        while (await r.ReadAsync()) list.Add(map(r));
        return list;
    }

    private async Task<T?> OneAsync<T>(string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] args)
    {
        var list = await QueryAsync(sql, map, args);
        return list.Count > 0 ? list[0] : default;
    }

    // ---------------- value readers ----------------

    private static string S(SqliteDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? "" : r.GetString(i);
    }

    private static string? SN(SqliteDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? null : r.GetString(i);
    }

    private static int I32(SqliteDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? 0 : Convert.ToInt32(r.GetValue(i));
    }

    private static long I64(SqliteDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? 0L : Convert.ToInt64(r.GetValue(i));
    }

    private static double Dbl(SqliteDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? 0.0 : Convert.ToDouble(r.GetValue(i));
    }

    private static bool Bool(SqliteDataReader r, string col) => I64(r, col) != 0;

    private static DateTime DT(SqliteDataReader r, string col)
    {
        var s = SN(r, col);
        return DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d)
            ? d : DateTime.MinValue;
    }

    private static string Iso(DateTime d) => d.ToString("o");

    private static string? IsoN(DateTime? d) => d is { } v ? v.ToString("o") : null;

    // ---------------- row mappers (ordinal based, null safe) ----------------

    private static Project ReadProject(SqliteDataReader r) => new(
        I32(r, "id"), S(r, "name"), SN(r, "description"), S(r, "client"), S(r, "status"),
        DT(r, "created"), DT(r, "updated"));

    private static Finding ReadFinding(SqliteDataReader r) => new(
        I32(r, "id"), I32(r, "project_id"), S(r, "title"), S(r, "severity"), SN(r, "description"),
        SN(r, "impact"), SN(r, "poc"), SN(r, "remediation"), DT(r, "created"));

    private static NoteItem ReadNote(SqliteDataReader r) => new(
        I32(r, "id"), I32(r, "project_id"), S(r, "title"), S(r, "body"), DT(r, "created"));

    private static ScanRecord ReadScan(SqliteDataReader r) => new(
        I32(r, "id"),
        r.IsDBNull(r.GetOrdinal("project_id")) ? null : I32(r, "project_id"),
        S(r, "tool"), S(r, "target"), DT(r, "started"), I32(r, "duration_ms"),
        S(r, "summary"), SN(r, "raw_path"));

    private static ReportRecord ReadReport(SqliteDataReader r) => new(
        I32(r, "id"),
        r.IsDBNull(r.GetOrdinal("project_id")) ? null : I32(r, "project_id"),
        S(r, "title"), S(r, "format"), S(r, "path"), DT(r, "created"), S(r, "author"));

    private static SessionRecord ReadSession(SqliteDataReader r) => new(
        I32(r, "id"), S(r, "name"), S(r, "kind"), S(r, "target"), DT(r, "started"),
        r.IsDBNull(r.GetOrdinal("ended")) ? null : DT(r, "ended"), SN(r, "notes"));

    private static LogEntry ReadLog(SqliteDataReader r) => new(
        I32(r, "id"), DT(r, "time"), S(r, "level"), S(r, "source"), S(r, "message"));

    private static ArsenalTemplate ReadTemplate(SqliteDataReader r) => new(
        I32(r, "id"), S(r, "name"), S(r, "category"), S(r, "command"), S(r, "description"));

    private static MacProfile ReadMacProfile(SqliteDataReader r) => new(
        I32(r, "id"), S(r, "name"), S(r, "interface_guid"), S(r, "mac"), DT(r, "created"));

    private static HandshakeInfo ReadHandshake(SqliteDataReader r)
    {
        var json = SN(r, "clients");
        List<string>? clients = null;
        if (!string.IsNullOrWhiteSpace(json))
        {
            try { clients = JsonSerializer.Deserialize<List<string>>(json); }
            catch { clients = null; }
        }
        return new HandshakeInfo(
            I32(r, "id"), S(r, "file_path"), SN(r, "ssid"), SN(r, "bssid"),
            I32(r, "eapol_frames"), Bool(r, "has_handshake"), clients ?? new List<string>(), DT(r, "analyzed"));
    }

    private static GeoPoint ReadGeoPoint(SqliteDataReader r) => new(
        I32(r, "id"), S(r, "kind"), S(r, "label"), SN(r, "bssid"), SN(r, "mac"),
        r.IsDBNull(r.GetOrdinal("lat")) ? null : Dbl(r, "lat"),
        r.IsDBNull(r.GetOrdinal("lon")) ? null : Dbl(r, "lon"),
        DT(r, "added"));

    // ---------------- projects ----------------

    public Task<List<Project>> GetProjectsAsync() =>
        QueryAsync("SELECT * FROM projects ORDER BY updated DESC", ReadProject);

    public async Task<Project> CreateProjectAsync(string name, string? description, string client)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Project name is required.", nameof(name));
        var now = Iso(DateTime.Now);
        var id = await InsertAsync(
            "INSERT INTO projects(name, description, client, status, created, updated) VALUES($name, $description, $client, 'Active', $created, $updated)",
            ("$name", name.Trim()), ("$description", description), ("$client", client ?? ""), ("$created", now), ("$updated", now));
        _log.Info("db", $"project created #{id}: {name.Trim()}");
        return await OneAsync("SELECT * FROM projects WHERE id = $id", ReadProject, ("$id", id))
               ?? new Project((int)id, name.Trim(), description, client ?? "", "Active", DateTime.Now, DateTime.Now);
    }

    public Task UpdateProjectAsync(Project p) => ExecAsync(
        "UPDATE projects SET name = $name, description = $description, client = $client, status = $status, updated = $updated WHERE id = $id",
        ("$name", p.Name), ("$description", p.Description), ("$client", p.Client), ("$status", p.Status),
        ("$updated", Iso(DateTime.Now)), ("$id", p.Id));

    public async Task DeleteProjectAsync(int id)
    {
        await ExecAsync("DELETE FROM findings WHERE project_id = $id", ("$id", id));
        await ExecAsync("DELETE FROM notes WHERE project_id = $id", ("$id", id));
        await ExecAsync("DELETE FROM projects WHERE id = $id", ("$id", id));
        _log.Info("db", $"project #{id} deleted (with child findings/notes)");
    }

    // ---------------- findings ----------------

    public Task<List<Finding>> GetFindingsAsync(int projectId) =>
        QueryAsync("SELECT * FROM findings WHERE project_id = $id ORDER BY id DESC", ReadFinding, ("$id", projectId));

    public async Task<Finding> AddFindingAsync(Finding f)
    {
        var created = f.Created == default ? DateTime.Now : f.Created;
        var id = await InsertAsync(
            "INSERT INTO findings(project_id, title, severity, description, impact, poc, remediation, created) VALUES($pid, $title, $severity, $description, $impact, $poc, $remediation, $created)",
            ("$pid", f.ProjectId), ("$title", f.Title), ("$severity", f.Severity), ("$description", f.Description),
            ("$impact", f.Impact), ("$poc", f.Poc), ("$remediation", f.Remediation), ("$created", Iso(created)));
        return new Finding((int)id, f.ProjectId, f.Title, f.Severity, f.Description, f.Impact, f.Poc, f.Remediation, created);
    }

    public Task UpdateFindingAsync(Finding f) => ExecAsync(
        "UPDATE findings SET project_id = $pid, title = $title, severity = $severity, description = $description, impact = $impact, poc = $poc, remediation = $remediation WHERE id = $id",
        ("$pid", f.ProjectId), ("$title", f.Title), ("$severity", f.Severity), ("$description", f.Description),
        ("$impact", f.Impact), ("$poc", f.Poc), ("$remediation", f.Remediation), ("$id", f.Id));

    public Task DeleteFindingAsync(int id) =>
        ExecAsync("DELETE FROM findings WHERE id = $id", ("$id", id));

    // ---------------- notes ----------------

    public Task<List<NoteItem>> GetNotesAsync(int projectId) =>
        QueryAsync("SELECT * FROM notes WHERE project_id = $id ORDER BY id DESC", ReadNote, ("$id", projectId));

    public async Task<NoteItem> AddNoteAsync(NoteItem n)
    {
        var created = n.Created == default ? DateTime.Now : n.Created;
        var id = await InsertAsync(
            "INSERT INTO notes(project_id, title, body, created) VALUES($pid, $title, $body, $created)",
            ("$pid", n.ProjectId), ("$title", n.Title), ("$body", n.Body), ("$created", Iso(created)));
        return new NoteItem((int)id, n.ProjectId, n.Title, n.Body, created);
    }

    public Task UpdateNoteAsync(NoteItem n) => ExecAsync(
        "UPDATE notes SET project_id = $pid, title = $title, body = $body WHERE id = $id",
        ("$pid", n.ProjectId), ("$title", n.Title), ("$body", n.Body), ("$id", n.Id));

    public Task DeleteNoteAsync(int id) =>
        ExecAsync("DELETE FROM notes WHERE id = $id", ("$id", id));

    // ---------------- scans ----------------

    public Task AddScanAsync(ScanRecord r) => ExecAsync(
        "INSERT INTO scans(project_id, tool, target, started, duration_ms, summary, raw_path) VALUES($pid, $tool, $target, $started, $duration, $summary, $raw)",
        ("$pid", r.ProjectId), ("$tool", r.Tool), ("$target", r.Target), ("$started", Iso(r.Started)),
        ("$duration", r.DurationMs), ("$summary", r.Summary), ("$raw", r.RawPath));

    public async Task<List<ScanRecord>> GetScansAsync(int? projectId = null, int limit = 200)
    {
        if (projectId is int pid)
            return await QueryAsync("SELECT * FROM scans WHERE project_id = $pid ORDER BY id DESC LIMIT $limit",
                ReadScan, ("$pid", pid), ("$limit", limit));
        return await QueryAsync("SELECT * FROM scans ORDER BY id DESC LIMIT $limit",
            ReadScan, ("$limit", limit));
    }

    // ---------------- reports ----------------

    public Task AddReportAsync(ReportRecord r) => ExecAsync(
        "INSERT INTO reports(project_id, title, format, path, created, author) VALUES($pid, $title, $format, $path, $created, $author)",
        ("$pid", r.ProjectId), ("$title", r.Title), ("$format", r.Format), ("$path", r.Path),
        ("$created", Iso(r.Created == default ? DateTime.Now : r.Created)), ("$author", r.Author));

    public async Task<List<ReportRecord>> GetReportsAsync(int? projectId = null)
    {
        if (projectId is int pid)
            return await QueryAsync("SELECT * FROM reports WHERE project_id = $pid ORDER BY id DESC",
                ReadReport, ("$pid", pid));
        return await QueryAsync("SELECT * FROM reports ORDER BY id DESC", ReadReport);
    }

    // ---------------- sessions ----------------

    public async Task<SessionRecord> StartSessionAsync(string name, string kind, string target)
    {
        var id = await InsertAsync(
            "INSERT INTO sessions(name, kind, target, started) VALUES($name, $kind, $target, $started)",
            ("$name", name), ("$kind", kind), ("$target", target ?? ""), ("$started", Iso(DateTime.Now)));
        return new SessionRecord((int)id, name, kind, target ?? "", DateTime.Now, null, null);
    }

    public Task EndSessionAsync(int id) => ExecAsync(
        "UPDATE sessions SET ended = $ended WHERE id = $id AND ended IS NULL",
        ("$ended", Iso(DateTime.Now)), ("$id", id));

    public Task<List<SessionRecord>> GetSessionsAsync(int limit = 100) =>
        QueryAsync("SELECT * FROM sessions ORDER BY id DESC LIMIT $limit", ReadSession, ("$limit", limit));

    /// <summary>Hard delete of a session row (Sessions module convenience; not part of IDbService).</summary>
    public Task DeleteSessionAsync(int id) =>
        ExecAsync("DELETE FROM sessions WHERE id = $id", ("$id", id));

    // ---------------- logs ----------------

    public Task AddLogDbAsync(LogEntry e) => ExecAsync(
        "INSERT INTO logs(time, level, source, message) VALUES($time, $level, $source, $message)",
        ("$time", IsoN(e.Time) ?? Iso(DateTime.Now)), ("$level", e.Level), ("$source", e.Source), ("$message", e.Message));

    public Task<List<LogEntry>> GetLogsAsync(int limit = 500) =>
        QueryAsync("SELECT * FROM logs ORDER BY id DESC LIMIT $limit", ReadLog, ("$limit", limit));

    // ---------------- arsenal templates ----------------

    public Task<List<ArsenalTemplate>> GetTemplatesAsync() =>
        QueryAsync("SELECT * FROM arsenal ORDER BY id", ReadTemplate);

    public async Task<int> SaveTemplateAsync(ArsenalTemplate t)
    {
        if (t.Id == 0)
        {
            var id = await InsertAsync(
                "INSERT INTO arsenal(name, category, command, description) VALUES($name, $category, $command, $description)",
                ("$name", t.Name), ("$category", t.Category), ("$command", t.Command), ("$description", t.Description));
            return (int)id;
        }
        await ExecAsync(
            "UPDATE arsenal SET name = $name, category = $category, command = $command, description = $description WHERE id = $id",
            ("$name", t.Name), ("$category", t.Category), ("$command", t.Command), ("$description", t.Description), ("$id", t.Id));
        return t.Id;
    }

    public Task DeleteTemplateAsync(int id) =>
        ExecAsync("DELETE FROM arsenal WHERE id = $id", ("$id", id));

    // ---------------- MAC profiles ----------------

    public Task<List<MacProfile>> GetMacProfilesAsync() =>
        QueryAsync("SELECT * FROM mac_profiles ORDER BY id DESC", ReadMacProfile);

    public async Task<int> SaveMacProfileAsync(MacProfile p)
    {
        if (p.Id == 0)
        {
            var id = await InsertAsync(
                "INSERT INTO mac_profiles(name, interface_guid, mac, created) VALUES($name, $guid, $mac, $created)",
                ("$name", p.Name), ("$guid", p.InterfaceGuid), ("$mac", p.Mac),
                ("$created", Iso(p.Created == default ? DateTime.Now : p.Created)));
            return (int)id;
        }
        await ExecAsync(
            "UPDATE mac_profiles SET name = $name, interface_guid = $guid, mac = $mac WHERE id = $id",
            ("$name", p.Name), ("$guid", p.InterfaceGuid), ("$mac", p.Mac), ("$id", p.Id));
        return p.Id;
    }

    public Task DeleteMacProfileAsync(int id) =>
        ExecAsync("DELETE FROM mac_profiles WHERE id = $id", ("$id", id));

    // ---------------- handshakes ----------------

    public async Task<int> AddHandshakeAsync(HandshakeInfo h)
    {
        var clientsJson = JsonSerializer.Serialize(h.ClientMacs ?? new List<string>());
        var id = await InsertAsync(
            "INSERT INTO handshakes(file_path, ssid, bssid, eapol_frames, has_handshake, clients, analyzed) VALUES($path, $ssid, $bssid, $eapol, $has, $clients, $analyzed)",
            ("$path", h.FilePath), ("$ssid", h.Ssid), ("$bssid", h.Bssid), ("$eapol", h.EapolFrames),
            ("$has", h.HasHandshake ? 1 : 0), ("$clients", clientsJson),
            ("$analyzed", Iso(h.Analyzed == default ? DateTime.Now : h.Analyzed)));
        return (int)id;
    }

    public Task<List<HandshakeInfo>> GetHandshakesAsync() =>
        QueryAsync("SELECT * FROM handshakes ORDER BY id DESC", ReadHandshake);

    public Task DeleteHandshakeAsync(int id) =>
        ExecAsync("DELETE FROM handshakes WHERE id = $id", ("$id", id));

    // ---------------- geomac ----------------

    public async Task<int> AddGeoPointAsync(GeoPoint p)
    {
        var id = await InsertAsync(
            "INSERT INTO geomac(kind, label, bssid, mac, lat, lon, added) VALUES($kind, $label, $bssid, $mac, $lat, $lon, $added)",
            ("$kind", p.Kind), ("$label", p.Label), ("$bssid", p.Bssid), ("$mac", p.Mac),
            ("$lat", p.Lat), ("$lon", p.Lon),
            ("$added", Iso(p.Added == default ? DateTime.Now : p.Added)));
        return (int)id;
    }

    public Task<List<GeoPoint>> GetGeoPointsAsync() =>
        QueryAsync("SELECT * FROM geomac ORDER BY id DESC", ReadGeoPoint);

    public Task DeleteGeoPointAsync(int id) =>
        ExecAsync("DELETE FROM geomac WHERE id = $id", ("$id", id));

    // ---------------- stats & backup ----------------

    public async Task<DashboardStats> GetStatsAsync()
    {
        using var con = await OpenAsync();

        long Count(string table)
        {
            using var cmd = Cmd(con, $"SELECT COUNT(*) FROM {table}");
            return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
        }

        long critical;
        using (var cmd = Cmd(con, "SELECT COUNT(*) FROM findings WHERE severity = 'critical'"))
        {
            critical = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
        }

        return new DashboardStats(
            Projects: (int)Count("projects"),
            Scans: (int)Count("scans"),
            Findings: (int)Count("findings"),
            CriticalFindings: (int)critical,
            Reports: (int)Count("reports"),
            Sessions: (int)Count("sessions"),
            Handshakes: (int)Count("handshakes"),
            MacChanges: (int)Count("mac_profiles"),
            Templates: (int)Count("arsenal"),
            GeoPoints: (int)Count("geomac"));
    }

    public async Task<string> BackupProjectAsync(int projectId, string outDir)
    {
        var project = await OneAsync("SELECT * FROM projects WHERE id = $id", ReadProject, ("$id", projectId))
                      ?? throw new InvalidOperationException($"Project #{projectId} not found.");
        var findings = await GetFindingsAsync(projectId);
        var notes = await GetNotesAsync(projectId);

        // ---- markdown rendering (project + findings + notes) ----
        var md = new StringBuilder();
        md.AppendLine($"# {project.Name}");
        md.AppendLine();
        md.AppendLine($"- **Client:** {project.Client}");
        md.AppendLine($"- **Status:** {project.Status}");
        md.AppendLine($"- **Created:** {project.Created:yyyy-MM-dd HH:mm}");
        md.AppendLine($"- **Updated:** {project.Updated:yyyy-MM-dd HH:mm}");
        if (!string.IsNullOrWhiteSpace(project.Description))
        {
            md.AppendLine();
            md.AppendLine("## Description");
            md.AppendLine();
            md.AppendLine(project.Description);
        }
        md.AppendLine();
        md.AppendLine($"## Findings ({findings.Count})");
        md.AppendLine();
        foreach (var g in findings
                     .GroupBy(f => (f.Severity ?? "info").ToLowerInvariant())
                     .OrderBy(g => SevRank(g.Key)))
        {
            md.AppendLine($"### {g.Key.ToUpperInvariant()} ({g.Count()})");
            md.AppendLine();
            foreach (var f in g)
            {
                md.AppendLine($"#### {f.Title}");
                md.AppendLine();
                if (!string.IsNullOrWhiteSpace(f.Description)) { md.AppendLine(f.Description); md.AppendLine(); }
                if (!string.IsNullOrWhiteSpace(f.Impact)) { md.AppendLine($"**Impact:** {f.Impact}"); md.AppendLine(); }
                if (!string.IsNullOrWhiteSpace(f.Poc))
                {
                    md.AppendLine("**Proof of concept:**");
                    md.AppendLine();
                    md.AppendLine("```");
                    md.AppendLine(f.Poc);
                    md.AppendLine("```");
                    md.AppendLine();
                }
                if (!string.IsNullOrWhiteSpace(f.Remediation)) { md.AppendLine($"**Remediation:** {f.Remediation}"); md.AppendLine(); }
            }
        }
        md.AppendLine($"## Notes ({notes.Count})");
        md.AppendLine();
        foreach (var n in notes)
        {
            md.AppendLine($"### {n.Title}");
            md.AppendLine();
            md.AppendLine(n.Body);
            md.AppendLine();
        }

        // ---- zip container ----
        Directory.CreateDirectory(outDir);
        var zipPath = Path.Combine(outDir, $"project_{projectId}_{DateTime.Now:yyyyMMddHHmmss}.zip");
        using (var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var mdEntry = zip.CreateEntry("report.md", CompressionLevel.Optimal);
            using (var w = new StreamWriter(mdEntry.Open(), new UTF8Encoding(false)))
                await w.WriteAsync(md.ToString());

            var fEntry = zip.CreateEntry("findings.json", CompressionLevel.Optimal);
            using (var w = new StreamWriter(fEntry.Open(), new UTF8Encoding(false)))
                await w.WriteAsync(JsonSerializer.Serialize(findings, new JsonSerializerOptions { WriteIndented = true }));

            var nEntry = zip.CreateEntry("notes.json", CompressionLevel.Optimal);
            using (var w = new StreamWriter(nEntry.Open(), new UTF8Encoding(false)))
                await w.WriteAsync(JsonSerializer.Serialize(notes, new JsonSerializerOptions { WriteIndented = true }));
        }
        _log.Info("db", $"project backup written: {zipPath}");
        return zipPath;
    }

    private static int SevRank(string sev) => sev switch
    {
        "critical" => 0,
        "high" => 1,
        "medium" => 2,
        "low" => 3,
        "info" => 4,
        _ => 5
    };
}
