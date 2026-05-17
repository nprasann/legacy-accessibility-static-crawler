using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using CsvHelper;
using LegacyAccessibilityCrawler.Core;
using Scriban;

namespace LegacyAccessibilityCrawler.Reporting;

public sealed class ReportGenerator : IReportGenerator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public async Task<ReportManifest> GenerateAsync(ScanResult result, string outputDirectory, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);
        var jsonPath = Path.Combine(outputDirectory, "report.json");
        var htmlPath = Path.Combine(outputDirectory, "report.html");
        var markdownPath = Path.Combine(outputDirectory, "report.md");
        var summaryPath = Path.Combine(outputDirectory, "executive-summary.md");
        var findingsCsvPath = Path.Combine(outputDirectory, "findings.csv");
        var adoPath = Path.Combine(outputDirectory, "ado-items.csv");

        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(result, JsonOptions), cancellationToken);
        await File.WriteAllTextAsync(markdownPath, RenderMarkdown(result), cancellationToken);
        await File.WriteAllTextAsync(summaryPath, RenderExecutiveSummary(result), cancellationToken);
        await File.WriteAllTextAsync(htmlPath, RenderHtml(result), cancellationToken);
        await WriteFindingsCsvAsync(findingsCsvPath, result.Findings, cancellationToken);
        await WriteAdoCsvAsync(adoPath, result.Findings, cancellationToken);

        return new ReportManifest(jsonPath, htmlPath, markdownPath, summaryPath, findingsCsvPath, adoPath);
    }

    private static string RenderMarkdown(ScanResult result)
    {
        var templateText = TryLoadTemplate("report.md.sbn") ?? DefaultMarkdownTemplate;
        return Template.Parse(templateText).Render(ToTemplateModel(result), member => member.Name);
    }

    private static string RenderExecutiveSummary(ScanResult result)
    {
        var templateText = TryLoadTemplate("executive-summary.md.sbn") ?? DefaultExecutiveTemplate;
        return Template.Parse(templateText).Render(ToTemplateModel(result), member => member.Name);
    }

    private static string RenderHtml(ScanResult result)
    {
        var templateText = TryLoadTemplate("report.html.sbn") ?? DefaultHtmlTemplate;
        return Template.Parse(templateText).Render(ToHtmlTemplateModel(result), member => member.Name);
    }

    private static async Task WriteFindingsCsvAsync(string path, IReadOnlyList<AccessibilityFinding> findings, CancellationToken cancellationToken)
    {
        await using var stream = File.Create(path);
        await using var writer = new StreamWriter(stream, Encoding.UTF8);
        using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);
        await csv.WriteRecordsAsync(findings.Select(f => new
        {
            f.FindingId,
            PageUrl = CsvSafe(f.PageUrl),
            RuleId = CsvSafe(f.RuleId),
            Standard = CsvSafe(f.Standard),
            SuccessCriterion = CsvSafe(f.SuccessCriterion),
            IssueType = CsvSafe(f.IssueType),
            Severity = f.Severity.ToString(),
            f.Confidence,
            Evidence = CsvSafe(f.Evidence),
            Selector = CsvSafe(f.Selector),
            f.NeedsManualReview,
            RemediationHint = CsvSafe(f.RemediationHint),
            Source = CsvSafe(f.Source),
            BrowserMode = f.BrowserMode.ToString()
        }), cancellationToken);
    }

    private static async Task WriteAdoCsvAsync(string path, IReadOnlyList<AccessibilityFinding> findings, CancellationToken cancellationToken)
    {
        await using var stream = File.Create(path);
        await using var writer = new StreamWriter(stream, Encoding.UTF8);
        using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);
        await csv.WriteRecordsAsync(findings.Select(f => new
        {
            WorkItemType = "User Story",
            Title = CsvSafe($"Fix accessibility issue: {f.IssueType.Replace('-', ' ')}"),
            Description = CsvSafe($"{f.Evidence}\n\nRule: {f.RuleId} {f.Standard} {f.SuccessCriterion}\nSelector: {f.Selector}\nSource URL: {f.PageUrl}"),
            AcceptanceCriteria = "- The affected element meets the referenced accessibility rule.\n- Keyboard navigation still works.\n- The same crawler check no longer reports the issue.\n- Manual accessibility validation is completed where required.",
            Severity = f.Severity.ToString(),
            Tags = CsvSafe($"accessibility; {f.Standard}; {f.RuleId}; {(f.NeedsManualReview ? "manual-review" : "static-check")}"),
            SourceUrl = CsvSafe(f.PageUrl),
            RuleReference = CsvSafe($"{f.Standard} {f.SuccessCriterion} {f.RuleId}"),
            Evidence = CsvSafe(f.Evidence)
        }), cancellationToken);
    }

    private static string CsvSafe(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        return value[0] is '=' or '+' or '-' or '@' ? $"'{value}" : value;
    }

    private static object ToTemplateModel(ScanResult result)
    {
        var bySeverity = result.Findings.GroupBy(f => f.Severity).OrderByDescending(g => g.Key).Select(g => new { severity = g.Key.ToString(), count = g.Count() }).ToList();
        var byCriterion = result.Findings.Where(f => !string.IsNullOrWhiteSpace(f.SuccessCriterion)).GroupBy(f => f.SuccessCriterion).OrderByDescending(g => g.Count()).Select(g => new { criterion = g.Key, count = g.Count() }).ToList();
        var recurring = result.Findings.GroupBy(f => f.IssueType).OrderByDescending(g => g.Count()).Take(10).Select(g => new { issue = g.Key, count = g.Count() }).ToList();
        return new
        {
            scan = result,
            product = ProductInfo.Name,
            version = ProductInfo.Version,
            commit = ProductInfo.Commit,
            build_date_utc = ProductInfo.BuildDateUtc,
            page_count = result.Pages.Count,
            finding_count = result.Findings.Count,
            manual_review_count = result.Findings.Count(f => f.NeedsManualReview),
            by_severity = bySeverity,
            by_criterion = byCriterion,
            recurring = recurring,
            ie_mode = result.Options.BrowserMode == BrowserMode.EdgeIeModeAssisted,
            disclaimer = result.Disclaimer
        };
    }

    private static object ToHtmlTemplateModel(ScanResult result)
    {
        static string H(string? value) => HtmlEncoder.Default.Encode(value ?? "");
        var findings = result.Findings.Select(f => new
        {
            severity = H(f.Severity.ToString()),
            page_url = H(f.PageUrl),
            issue_type = H(f.IssueType),
            rule_id = H(f.RuleId),
            evidence = H(f.Evidence),
            needs_manual_review = H(f.NeedsManualReview.ToString())
        }).ToList();

        return new
        {
            product = H(ProductInfo.Name),
            version = H(ProductInfo.Version),
            commit = H(ProductInfo.Commit),
            build_date_utc = H(ProductInfo.BuildDateUtc),
            page_count = result.Pages.Count,
            finding_count = result.Findings.Count,
            manual_review_count = result.Findings.Count(f => f.NeedsManualReview),
            disclaimer = H(result.Disclaimer),
            scan = new { findings }
        };
    }

    private static string? TryLoadTemplate(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "templates", fileName);
        if (File.Exists(path))
        {
            return File.ReadAllText(path);
        }

        var repoPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "templates", fileName));
        return File.Exists(repoPath) ? File.ReadAllText(repoPath) : null;
    }

    private const string DefaultMarkdownTemplate = """
        # Accessibility Assessment Report

        Tool: {{ product }} {{ version }}

        ## Executive summary
        Pages crawled: {{ page_count }}
        Total findings: {{ finding_count }}
        Findings requiring manual review: {{ manual_review_count }}

        ## Standards and rule sources
        Built-in WCAG 2.1 AA static baseline, optional Section 508 baseline, optional WCAG 2.2 enhanced rules, and optional PDF-derived guidance overlays.

        ## Findings by severity
        {{ for item in by_severity }}- {{ item.severity }}: {{ item.count }}
        {{ end }}

        ## Findings by WCAG success criterion
        {{ for item in by_criterion }}- {{ item.criterion }}: {{ item.count }}
        {{ end }}

        ## Top recurring issues
        {{ for item in recurring }}- {{ item.issue }}: {{ item.count }}
        {{ end }}

        ## IE-mode limitations
        {{ if ie_mode }}{{ scan.disclaimer }}
        {{ else }}Not applicable for this scan mode.
        {{ end }}

        ## Per-page findings
        {{ for page in scan.pages }}
        ### {{ page.sanitized_url }}
        {{ for finding in scan.findings }}{{ if finding.page_url == page.sanitized_url }}- [{{ finding.severity }}] {{ finding.issue_type }} ({{ finding.rule_id }}): {{ finding.evidence }}
        {{ end }}{{ end }}
        {{ end }}

        ## Disclaimer
        {{ disclaimer }}
        """;

    private const string DefaultExecutiveTemplate = """
        # Executive Summary

        {{ product }} {{ version }} assessed {{ page_count }} page(s) and produced {{ finding_count }} static finding(s). {{ manual_review_count }} finding(s) require manual accessibility review.

        This output assists remediation planning and modernization backlog creation. It does not certify ADA, WCAG, or Section 508 compliance.
        """;

    private const string DefaultHtmlTemplate = """
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <title>Accessibility Assessment Report</title>
          <style>
            body { font-family: system-ui, sans-serif; margin: 2rem; color: #1f2937; }
            table { border-collapse: collapse; width: 100%; margin-block: 1rem; }
            th, td { border: 1px solid #d1d5db; padding: .5rem; text-align: left; vertical-align: top; }
            th { background: #f3f4f6; }
            .disclaimer { border-left: 4px solid #b45309; background: #fffbeb; padding: 1rem; }
          </style>
        </head>
        <body>
          <h1>Accessibility Assessment Report</h1>
          <p><strong>{{ product }}</strong> {{ version }} | Commit {{ commit }} | Build {{ build_date_utc }}</p>
          <section class="disclaimer">{{ disclaimer }}</section>
          <h2>Summary</h2>
          <p>Pages crawled: {{ page_count }}. Findings: {{ finding_count }}. Manual review required: {{ manual_review_count }}.</p>
          <h2>Findings</h2>
          <table>
            <thead><tr><th>Severity</th><th>Page</th><th>Issue</th><th>Rule</th><th>Evidence</th><th>Manual</th></tr></thead>
            <tbody>
            {{ for finding in scan.findings }}
              <tr><td>{{ finding.severity }}</td><td>{{ finding.page_url }}</td><td>{{ finding.issue_type }}</td><td>{{ finding.rule_id }}</td><td>{{ finding.evidence }}</td><td>{{ finding.needs_manual_review }}</td></tr>
            {{ end }}
            </tbody>
          </table>
        </body>
        </html>
        """;
}
