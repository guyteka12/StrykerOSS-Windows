using System.Text;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace StrykerOSS.Core.Services;

/// <summary>
/// Real report generation: PDF via QuestPDF (Community license), plus self-contained
/// dark HTML and plain Markdown. Every generated file is registered in the reports table.
/// </summary>
public sealed class ReportService : IReportService
{
    private readonly ISettings _settings;
    private readonly ILogger _log;
    private readonly IDbService _db;

    static ReportService()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public ReportService(ISettings settings, ILogger log, IDbService db)
    {
        _settings = settings;
        _log = log;
        _db = db;
    }

    public async Task<string> GenerateAsync(Project? project, IList<Finding> findings, string title,
        string author, string? execSummary, string fmt, string outDir, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("Le titre du rapport est requis.", nameof(title));
        ct.ThrowIfCancellationRequested();

        Directory.CreateDirectory(outDir);
        var list = findings ?? new List<Finding>();

        var kind = (fmt ?? "pdf").Trim().ToLowerInvariant();
        var normalized = kind switch
        {
            "html" => "html",
            "markdown" => "markdown",
            "md" => "markdown",
            _ => "pdf"
        };
        var ext = normalized switch
        {
            "html" => "html",
            "markdown" => "md",
            _ => "pdf"
        };

        var safe = SafeName(title);
        var path = Path.Combine(outDir, $"{safe}_{DateTime.Now:yyyyMMdd_HHmmss}.{ext}");

        switch (normalized)
        {
            case "html":
                await File.WriteAllTextAsync(path, BuildHtml(project, list, title, author, execSummary), Encoding.UTF8, ct);
                break;
            case "markdown":
                await File.WriteAllTextAsync(path, BuildMarkdown(project, list, title, author, execSummary), Encoding.UTF8, ct);
                break;
            default:
                BuildPdf(project, list, title, author, execSummary, path);
                break;
        }

        var authorFinal = string.IsNullOrWhiteSpace(author)
            ? (string.IsNullOrWhiteSpace(_settings.Value.DefaultAuthor) ? "inconnu" : _settings.Value.DefaultAuthor)
            : author.Trim();

        await _db.AddReportAsync(new ReportRecord(0, project?.Id, title, normalized, path, DateTime.Now, authorFinal));
        _log.Info("reports", $"rapport {normalized} généré « {title} » -> {path}");
        return path;
    }

    private static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (var c in name.Trim())
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        var s = sb.ToString();
        if (string.IsNullOrWhiteSpace(s)) s = "report";
        return s.Length > 80 ? s[..80].TrimEnd() : s;
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

    private static Color SevColor(string sev) => sev switch
    {
        "critical" => Colors.Red.Darken1,
        "high" => Colors.Red.Medium,
        "medium" => Colors.Orange.Darken1,
        "low" => Colors.Blue.Medium,
        _ => Colors.Grey.Darken1
    };

    // ---------------- PDF (QuestPDF) ----------------

    private static void BuildPdf(Project? project, IList<Finding> findings, string title,
        string author, string? execSummary, string path)
    {
        var groups = findings
            .GroupBy(f => (f.Severity ?? "info").ToLowerInvariant())
            .OrderBy(g => SevRank(g.Key))
            .ToList();

        Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(36);
                page.DefaultTextStyle(t => t.FontSize(10));

                page.Header().Column(col =>
                {
                    col.Item().Text(title).FontSize(20).Bold();
                    col.Item().Text($"Auteur : {author}  •  {DateTime.Now:d}  •  Projet : {project?.Name ?? "—"}")
                        .FontSize(9).FontColor(Colors.Grey.Darken2);
                });

                page.Content().PaddingVertical(10).Column(col =>
                {
                    if (!string.IsNullOrWhiteSpace(execSummary))
                    {
                        col.Item().Text("Résumé exécutif").SemiBold().FontSize(13);
                        col.Item().Text(execSummary).FontSize(10);
                    }

                    col.Item().PaddingTop(10).Text($"Constats ({findings.Count})").SemiBold().FontSize(13);

                    foreach (var g in groups)
                    {
                        col.Item().PaddingTop(8)
                            .Text($"{g.Key.ToUpperInvariant()} ({g.Count()})")
                            .FontColor(SevColor(g.Key)).Bold();

                        foreach (var f in g)
                        {
                            col.Item().PaddingTop(6).Text(f.Title).SemiBold().FontSize(11);
                            if (!string.IsNullOrWhiteSpace(f.Description))
                                col.Item().Text(f.Description).FontSize(9).FontColor(Colors.Grey.Darken2);
                            if (!string.IsNullOrWhiteSpace(f.Impact))
                                col.Item().Text(t =>
                                {
                                    t.Span("Impact : ").SemiBold().FontSize(9);
                                    t.Span(f.Impact).FontSize(9);
                                });
                            if (!string.IsNullOrWhiteSpace(f.Poc))
                                col.Item().Text(t =>
                                {
                                    t.Span("Preuve de concept : ").SemiBold().FontSize(9);
                                    t.Span(f.Poc).FontFamily("Consolas").FontSize(9);
                                });
                            if (!string.IsNullOrWhiteSpace(f.Remediation))
                                col.Item().Text(t =>
                                {
                                    t.Span("Correctifs : ").SemiBold().FontSize(9);
                                    t.Span(f.Remediation).FontSize(9);
                                });
                        }
                    }
                });

                page.Footer().AlignCenter().Text(x =>
                {
                    x.Span("Page ");
                    x.CurrentPageNumber();
                    x.Span(" / ");
                    x.TotalPages();
                });
            });
        }).GeneratePdf(path);
    }

    // ---------------- HTML ----------------

    private static string BuildHtml(Project? project, IList<Finding> findings, string title,
        string author, string? execSummary)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>")
          .Append(Esc(title)).AppendLine("</title>");
        sb.AppendLine("""
<style>
body{background:#0B0B12;color:#E8E8F2;font-family:'Segoe UI',Arial,sans-serif;margin:0;padding:32px;}
h1{color:#FFFFFF;margin:0 0 4px 0;font-size:28px;}
h2{color:#A78BFA;font-size:18px;margin:28px 0 8px 0;border-bottom:1px solid #2A2A3C;padding-bottom:6px;}
h3{font-size:15px;margin:18px 0 6px 0;color:#E8E8F2;}
h4{font-size:14px;margin:14px 0 4px 0;}
.card{background:#14141F;border:1px solid #2A2A3C;border-radius:12px;padding:16px;margin:12px 0;}
.meta{color:#A9A9C0;font-size:13px;margin-bottom:16px;}
.badge{display:inline-block;border-radius:999px;padding:2px 10px;font-size:11px;font-weight:700;color:#0B0B12;}
.sev-critical{background:#EF4444;} .sev-high{background:#EF4444;} .sev-medium{background:#F59E0B;}
.sev-low{background:#38BDF8;} .sev-info{background:#6E6E88;}
pre{background:#1B1B29;border:1px solid #2A2A3C;border-radius:8px;padding:10px;overflow-x:auto;font-family:Consolas,monospace;font-size:12px;color:#E8E8F2;}
.label{color:#A78BFA;font-weight:600;font-size:12px;}
p{line-height:1.5;font-size:13px;}
.footer{color:#6E6E88;font-size:11px;margin-top:24px;}
</style>
""");
        sb.AppendLine("</head><body>");
        sb.AppendLine($"<h1>{Esc(title)}</h1>");
        sb.AppendLine($"<div class=\"meta\">Auteur : {Esc(author)} &nbsp;•&nbsp; {DateTime.Now:yyyy-MM-dd HH:mm} &nbsp;•&nbsp; Projet : {Esc(project?.Name ?? "—")}</div>");
        if (!string.IsNullOrWhiteSpace(execSummary))
            sb.AppendLine($"<div class=\"card\"><div class=\"label\">RÉSUMÉ EXÉCUTIF</div><p>{Esc(execSummary).Replace("\n", "<br/>")}</p></div>");
        sb.AppendLine($"<h2>Constats ({findings.Count})</h2>");
        foreach (var g in findings
                     .GroupBy(f => (f.Severity ?? "info").ToLowerInvariant())
                     .OrderBy(g => SevRank(g.Key)))
        {
            sb.AppendLine($"<h3><span class=\"badge sev-{Esc(g.Key)}\">{Esc(g.Key.ToUpperInvariant())}</span> &nbsp;{g.Count()} constat(s)</h3>");
            foreach (var f in g)
            {
                sb.AppendLine("<div class=\"card\">");
                sb.AppendLine($"<h4>{Esc(f.Title)}</h4>");
                if (!string.IsNullOrWhiteSpace(f.Description))
                    sb.AppendLine($"<p>{Esc(f.Description).Replace("\n", "<br/>")}</p>");
                if (!string.IsNullOrWhiteSpace(f.Impact))
                    sb.AppendLine($"<p><span class=\"label\">IMPACT :</span> {Esc(f.Impact)}</p>");
                if (!string.IsNullOrWhiteSpace(f.Poc))
                    sb.AppendLine($"<p><span class=\"label\">PREUVE DE CONCEPT</span></p><pre>{Esc(f.Poc)}</pre>");
                if (!string.IsNullOrWhiteSpace(f.Remediation))
                    sb.AppendLine($"<p><span class=\"label\">CORRECTIFS :</span> {Esc(f.Remediation)}</p>");
                sb.AppendLine("</div>");
            }
        }
        sb.AppendLine("<div class=\"footer\">StrykerOSS — rapport de test d'intrusion à but éducatif. Généré localement ; aucune donnée ne quitte cette machine.</div>");
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    private static string Esc(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;");
    }

    // ---------------- Markdown ----------------

    private static string BuildMarkdown(Project? project, IList<Finding> findings, string title,
        string author, string? execSummary)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {title}");
        sb.AppendLine();
        sb.AppendLine($"- **Auteur :** {author}");
        sb.AppendLine($"- **Date :** {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"- **Projet :** {project?.Name ?? "—"}");
        if (project is { Client.Length: > 0 })
            sb.AppendLine($"- **Client :** {project.Client}");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(execSummary))
        {
            sb.AppendLine("## Résumé exécutif");
            sb.AppendLine();
            sb.AppendLine(execSummary);
            sb.AppendLine();
        }
        sb.AppendLine($"## Constats ({findings.Count})");
        sb.AppendLine();
        foreach (var g in findings
                     .GroupBy(f => (f.Severity ?? "info").ToLowerInvariant())
                     .OrderBy(g => SevRank(g.Key)))
        {
            sb.AppendLine($"### {g.Key.ToUpperInvariant()} ({g.Count()})");
            sb.AppendLine();
            foreach (var f in g)
            {
                sb.AppendLine($"#### {f.Title}");
                sb.AppendLine();
                sb.AppendLine($"**Sévérité :** {f.Severity ?? "info"}");
                if (!string.IsNullOrWhiteSpace(f.Description)) { sb.AppendLine(); sb.AppendLine(f.Description); }
                if (!string.IsNullOrWhiteSpace(f.Impact)) { sb.AppendLine(); sb.AppendLine($"**Impact :** {f.Impact}"); }
                if (!string.IsNullOrWhiteSpace(f.Poc))
                {
                    sb.AppendLine();
                    sb.AppendLine("**Preuve de concept :**");
                    sb.AppendLine();
                    sb.AppendLine("```");
                    sb.AppendLine(f.Poc);
                    sb.AppendLine("```");
                }
                if (!string.IsNullOrWhiteSpace(f.Remediation)) { sb.AppendLine(); sb.AppendLine($"**Correctifs :** {f.Remediation}"); }
                sb.AppendLine();
            }
        }
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("*Généré par StrykerOSS — usage éducatif uniquement, sur des systèmes que vous êtes autorisé à tester.*");
        return sb.ToString();
    }
}
