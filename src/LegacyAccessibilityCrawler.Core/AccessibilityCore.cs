using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Net;

namespace LegacyAccessibilityCrawler.Core;

public static class ProductInfo
{
    public const string Name = "legacy-accessibility-static-crawler";
    public static string Version => typeof(ProductInfo).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    public static string Commit => ThisAssemblyMetadata("SourceRevisionId") ?? ShortSha(Environment.GetEnvironmentVariable("GITHUB_SHA")) ?? "local";
    public static string BuildDateUtc => ThisAssemblyMetadata("BuildDateUtc") ?? Environment.GetEnvironmentVariable("BUILD_DATE_UTC") ?? DateTime.UtcNow.ToString("O");

    private static string? ThisAssemblyMetadata(string key) =>
        typeof(ProductInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.OrdinalIgnoreCase))?.Value;

    private static string? ShortSha(string? sha) => string.IsNullOrWhiteSpace(sha) ? null : sha[..Math.Min(7, sha.Length)];
}

public enum BrowserMode
{
    ModernEdge,
    Chrome,
    EdgeIeModeAssisted
}

public enum Severity
{
    Info,
    Low,
    Medium,
    High,
    Critical
}

public sealed record CrawlerOptions
{
    public BrowserMode BrowserMode { get; init; } = BrowserMode.ModernEdge;
    public string? StartUrl { get; init; }
    public int MaxPages { get; init; } = 25;
    public int CrawlDepth { get; init; } = 2;
    public int DelaySeconds { get; init; } = 1;
    public bool SameDomainOnly { get; init; } = true;
    public bool RespectRobotsTxt { get; init; } = true;
    public string? RulesPdfPath { get; init; }
    public string OutputDirectory { get; init; } = "reports/latest";
    public bool CaptureScreenshots { get; init; } = true;
    public bool CaptureHtml { get; init; } = true;
    public bool EnableKeyboardChecks { get; init; } = true;
    public bool EnableWcag22EnhancedRules { get; init; }
    public bool EnableSection508Rules { get; init; } = true;
    public bool EnablePdfRuleOverlay { get; init; } = true;
    public bool RedactQueryStrings { get; init; } = true;
    public bool ManualSession { get; init; }
    public bool Headless { get; init; } = true;
    public bool AcceptInsecureCertificates { get; init; }
    public IReadOnlyList<string> AllowedDomains { get; init; } = [];
}

public sealed record OutputPathOptions
{
    public string BaseOutputDirectory { get; init; } = "reports";
}

public sealed record CrawlSecurityOptions
{
    public IReadOnlyList<string> AllowedDomains { get; init; } = [];
    public bool AllowPrivateNetworkTargets { get; init; }
    public int MaxPagesLimit { get; init; } = 100;
    public int MaxDepthLimit { get; init; } = 5;
    public int MaxDelaySecondsLimit { get; init; } = 30;
    public int JobTimeoutMinutes { get; init; } = 30;
}

public sealed record FileInputOptions
{
    public string BaseInputDirectory { get; init; } = "inputs";
    public long MaxPdfBytes { get; init; } = 25 * 1024 * 1024;
    public long MaxCsvBytes { get; init; } = 5 * 1024 * 1024;
}

public static class OutputPathSanitizer
{
    public static string ResolveOutputDirectory(string? requestedPath, string baseOutputDirectory)
    {
        var baseDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(baseOutputDirectory) ? "reports" : baseOutputDirectory);
        var relativeOrDefault = string.IsNullOrWhiteSpace(requestedPath) ? "latest" : requestedPath;

        if (relativeOrDefault.Contains('\0') || relativeOrDefault.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains(".."))
        {
            throw new ArgumentException("Output paths cannot contain traversal segments.", nameof(requestedPath));
        }

        if (IsUncPath(relativeOrDefault))
        {
            throw new ArgumentException("UNC/network output paths are not allowed.", nameof(requestedPath));
        }

        var resolved = Path.GetFullPath(relativeOrDefault);
        if (!Path.IsPathFullyQualified(relativeOrDefault) && !IsInsideDirectory(resolved, baseDirectory))
        {
            resolved = Path.GetFullPath(Path.Combine(baseDirectory, relativeOrDefault));
        }

        if (!IsInsideDirectory(resolved, baseDirectory))
        {
            throw new ArgumentException($"Output path must stay inside '{baseDirectory}'.", nameof(requestedPath));
        }

        return resolved;
    }

    private static bool IsUncPath(string path) => path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal);

    private static bool IsInsideDirectory(string candidate, string baseDirectory)
    {
        var normalizedBase = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory)) + Path.DirectorySeparatorChar;
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate)) + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(normalizedBase, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}

public static class CrawlRequestValidator
{
    public static Uri ValidateStartUrl(string? startUrl, IEnumerable<string> allowedDomains, bool allowPrivateNetworkTargets = false)
    {
        if (!Uri.TryCreate(startUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("StartUrl must be an absolute http or https URL.", nameof(startUrl));
        }

        if (!allowPrivateNetworkTargets && IsPrivateOrLocalTarget(uri.Host))
        {
            throw new ArgumentException("Private, loopback, and link-local crawl targets are disabled by default.");
        }

        var domains = allowedDomains.Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => d.Trim()).ToArray();
        if (domains.Length == 0)
        {
            throw new ArgumentException("At least one allowed domain must be configured before crawling.");
        }

        if (!domains.Any(domain => HostMatches(uri.Host, domain)))
        {
            throw new ArgumentException($"The URL host '{uri.Host}' is not in the configured allowed domain list.");
        }

        return uri;
    }

    public static CrawlerOptions ValidateAndNormalize(
        CrawlerOptions options,
        IEnumerable<string> configuredAllowedDomains,
        string baseOutputDirectory,
        bool allowPrivateNetworkTargets = false,
        int maxPagesLimit = 100,
        int maxDepthLimit = 5,
        int maxDelaySecondsLimit = 30)
    {
        ValidateStartUrl(options.StartUrl, configuredAllowedDomains, allowPrivateNetworkTargets);
        ValidateCrawlLimits(options, maxPagesLimit, maxDepthLimit, maxDelaySecondsLimit);
        var output = OutputPathSanitizer.ResolveOutputDirectory(options.OutputDirectory, baseOutputDirectory);
        return options with
        {
            OutputDirectory = output,
            AllowedDomains = configuredAllowedDomains.Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => d.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        };
    }

    private static void ValidateCrawlLimits(CrawlerOptions options, int maxPagesLimit, int maxDepthLimit, int maxDelaySecondsLimit)
    {
        if (options.MaxPages is < 1 || options.MaxPages > maxPagesLimit)
        {
            throw new ArgumentException($"MaxPages must be between 1 and {maxPagesLimit}.", nameof(options));
        }

        if (options.CrawlDepth is < 0 || options.CrawlDepth > maxDepthLimit)
        {
            throw new ArgumentException($"CrawlDepth must be between 0 and {maxDepthLimit}.", nameof(options));
        }

        if (options.DelaySeconds is < 0 || options.DelaySeconds > maxDelaySecondsLimit)
        {
            throw new ArgumentException($"DelaySeconds must be between 0 and {maxDelaySecondsLimit}.", nameof(options));
        }
    }

    private static bool HostMatches(string host, string pattern)
    {
        if (pattern.StartsWith("*.", StringComparison.Ordinal))
        {
            var suffix = pattern[1..];
            return host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && host.Length > suffix.Length;
        }

        return string.Equals(host, pattern, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPrivateOrLocalTarget(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host, out var address) &&
            (IPAddress.IsLoopback(address) || IsPrivateOrLinkLocal(address));
    }

    private static bool IsPrivateOrLinkLocal(IPAddress address)
    {
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10 ||
                bytes[0] == 127 ||
                bytes[0] == 169 && bytes[1] == 254 ||
                bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
                bytes[0] == 192 && bytes[1] == 168;
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || IPAddress.IsLoopback(address);
        }

        return false;
    }
}

public static class FileInputValidator
{
    public static string ResolveExistingFile(string requestedPath, string baseInputDirectory, string requiredExtension, long maxBytes)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
        {
            throw new ArgumentException("Input file path is required.", nameof(requestedPath));
        }

        if (Path.IsPathFullyQualified(requestedPath) || requestedPath.Contains('\0') ||
            requestedPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains(".."))
        {
            throw new ArgumentException("Input files must be relative paths inside the configured input directory.", nameof(requestedPath));
        }

        if (!string.Equals(Path.GetExtension(requestedPath), requiredExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Input file must use the {requiredExtension} extension.", nameof(requestedPath));
        }

        var baseDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(baseInputDirectory) ? "inputs" : baseInputDirectory);
        var resolved = Path.GetFullPath(Path.Combine(baseDirectory, requestedPath));
        var normalizedBase = Path.TrimEndingDirectorySeparator(baseDirectory) + Path.DirectorySeparatorChar;
        var normalizedResolved = Path.TrimEndingDirectorySeparator(resolved) + Path.DirectorySeparatorChar;
        if (!normalizedResolved.StartsWith(normalizedBase, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new ArgumentException("Input file path is outside the configured input directory.", nameof(requestedPath));
        }

        var info = new FileInfo(resolved);
        if (!info.Exists)
        {
            throw new ArgumentException("Input file was not found.", nameof(requestedPath));
        }

        if (info.Length > maxBytes)
        {
            throw new ArgumentException($"Input file exceeds the configured {maxBytes} byte size limit.", nameof(requestedPath));
        }

        return resolved;
    }
}

public sealed record AccessibilityRule
{
    public string RuleId { get; init; } = "";
    public string Standard { get; init; } = "";
    public string SuccessCriterion { get; init; } = "";
    public string Level { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public string Source { get; init; } = "built-in";
    public int? SourcePage { get; init; }
    public Severity SeverityDefault { get; init; } = Severity.Medium;
    public IReadOnlyList<string> Keywords { get; init; } = [];
    public IReadOnlyList<string> AppliesTo { get; init; } = [];
    public string TestMethod { get; init; } = "";
    public bool StaticCheckSupported { get; init; }
    public bool ManualReviewRequired { get; init; }
    public string RemediationGuidance { get; init; } = "";
    public IReadOnlyList<string> References { get; init; } = [];
    public string RawText { get; init; } = "";
}

public sealed record ElementEvidence(string Selector, string Text, string? AccessibleName = null, string? Href = null, string? Type = null);
public sealed record HeadingEvidence(int Level, string Text, string Selector);
public sealed record ImageEvidence(string Selector, string? Alt, string? Src, string? AccessibleName);
public sealed record InputEvidence(string Selector, string Type, string? Id, string? Name, string? Label, bool Required);
public sealed record TableEvidence(string Selector, bool HasHeader, bool HasCaption, bool IsComplex);
public sealed record AriaEvidence(string Selector, string Attribute, string? Value);
public sealed record LegacyRisk(string RiskType, string Evidence, Severity Severity);

public sealed record PageCapture
{
    public string Url { get; init; } = "";
    public string SanitizedUrl { get; init; } = "";
    public string Title { get; init; } = "";
    public BrowserMode BrowserMode { get; init; } = BrowserMode.ModernEdge;
    public string? ScreenshotPath { get; init; }
    public string? HtmlPath { get; init; }
    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string Html { get; init; } = "";
    public IReadOnlyList<HeadingEvidence> Headings { get; init; } = [];
    public IReadOnlyList<ElementEvidence> Links { get; init; } = [];
    public IReadOnlyList<ElementEvidence> Buttons { get; init; } = [];
    public IReadOnlyList<InputEvidence> Inputs { get; init; } = [];
    public IReadOnlyList<ElementEvidence> Labels { get; init; } = [];
    public IReadOnlyList<ImageEvidence> Images { get; init; } = [];
    public IReadOnlyList<TableEvidence> Tables { get; init; } = [];
    public IReadOnlyList<ElementEvidence> Iframes { get; init; } = [];
    public IReadOnlyList<AriaEvidence> AriaAttributes { get; init; } = [];
    public IReadOnlyList<ElementEvidence> FocusableElements { get; init; } = [];
    public string VisibleTextSummary { get; init; } = "";
    public IReadOnlyList<LegacyRisk> LegacyRisks { get; init; } = [];
    public bool DomCaptureLimited { get; init; }
}

public sealed record AccessibilityFinding
{
    public string FindingId { get; init; } = Guid.NewGuid().ToString("N");
    public string PageUrl { get; init; } = "";
    public string RuleId { get; init; } = "";
    public string Standard { get; init; } = "";
    public string SuccessCriterion { get; init; } = "";
    public string IssueType { get; init; } = "";
    public Severity Severity { get; init; } = Severity.Medium;
    public double Confidence { get; init; } = 1.0;
    public string Evidence { get; init; } = "";
    public string HtmlSnippet { get; init; } = "";
    public string Selector { get; init; } = "";
    public bool NeedsManualReview { get; init; }
    public string RemediationHint { get; init; } = "";
    public string Source { get; init; } = "static";
    public BrowserMode BrowserMode { get; init; }
}

public sealed record ManualFinding
{
    public string PageUrl { get; init; } = "";
    public string PageTitle { get; init; } = "";
    public BrowserMode BrowserMode { get; init; } = BrowserMode.EdgeIeModeAssisted;
    public string AssistiveTechnology { get; init; } = "";
    public string TestArea { get; init; } = "";
    public string IssueSummary { get; init; } = "";
    public string ObservedBehavior { get; init; } = "";
    public string ExpectedBehavior { get; init; } = "";
    public string RuleReference { get; init; } = "";
    public Severity Severity { get; init; } = Severity.Medium;
    public string ScreenshotPath { get; init; } = "";
    public string RemediationNotes { get; init; } = "";
}

public sealed record PdfExtractionResult(
    string SourcePath,
    string RawTextPath,
    string ChunksPath,
    string NormalizedRulesPath,
    IReadOnlyList<AccessibilityRule> Rules,
    IReadOnlyList<string> Warnings);

public sealed record ScanResult
{
    public string ScanId { get; init; } = Guid.NewGuid().ToString("N");
    public CrawlerOptions Options { get; init; } = new();
    public IReadOnlyList<PageCapture> Pages { get; init; } = [];
    public IReadOnlyList<AccessibilityFinding> Findings { get; init; } = [];
    public IReadOnlyList<ManualFinding> ManualFindings { get; init; } = [];
    public IReadOnlyList<AccessibilityRule> RulesUsed { get; init; } = [];
    public DateTimeOffset StartedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset CompletedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string Disclaimer { get; init; } = ComplianceDisclaimers.Standard;
    public string ToolVersion { get; init; } = ProductInfo.Version;
}

public sealed record ReportManifest(
    string JsonPath,
    string HtmlPath,
    string MarkdownPath,
    string ExecutiveSummaryPath,
    string FindingsCsvPath,
    string AdoCsvPath);

public interface IRulePackService
{
    Task<IReadOnlyList<AccessibilityRule>> LoadRulesAsync(bool includeSection508, bool includeWcag22, IEnumerable<AccessibilityRule>? pdfOverlayRules = null, CancellationToken cancellationToken = default);
}

public interface ICrawlerService
{
    Task<IReadOnlyList<PageCapture>> CrawlAsync(CrawlerOptions options, CancellationToken cancellationToken = default);
}

public interface IPdfRulesLoaderService
{
    Task<PdfExtractionResult> ExtractAsync(string pdfPath, string outputDirectory, CancellationToken cancellationToken = default);
}

public interface IStaticCheckEngine
{
    IReadOnlyList<AccessibilityFinding> Analyze(PageCapture page, IReadOnlyList<AccessibilityRule> rules);
}

public interface IRuleMatcherService
{
    IReadOnlyList<AccessibilityFinding> EnrichFindings(IEnumerable<AccessibilityFinding> findings, IReadOnlyList<AccessibilityRule> rules);
}

public interface IReportGenerator
{
    Task<ReportManifest> GenerateAsync(ScanResult result, string outputDirectory, CancellationToken cancellationToken = default);
}

public interface IManualFindingsImporter
{
    Task<IReadOnlyList<ManualFinding>> ImportAsync(string csvPath, CancellationToken cancellationToken = default);
}

public interface ILlmReviewService
{
    bool IsEnabled { get; }
    Task<string> ExplainFindingAsync(AccessibilityFinding finding, CancellationToken cancellationToken = default);
}

public sealed class DisabledLlmReviewService : ILlmReviewService
{
    public bool IsEnabled => false;
    public Task<string> ExplainFindingAsync(AccessibilityFinding finding, CancellationToken cancellationToken = default) =>
        Task.FromResult("LLM review is disabled. Static evidence and deterministic remediation guidance were used.");
}

public static class ComplianceDisclaimers
{
    public const string Standard =
        "This tool is an accessibility assessment assistant, not a compliance certification tool. " +
        "Automated static testing does not replace manual accessibility testing, screen reader testing, keyboard testing, user-flow testing, or assistive technology validation. " +
        "Static checks cannot prove full WCAG, ADA Title II, or Section 508 compliance.";

    public const string IeMode =
        "Edge IE-mode-assisted scans have automation limitations. DOM inspection, control state, legacy plug-ins, ActiveX content, and keyboard behavior may require manual validation. " +
        "A Legacy IE Mode Manual Review Required finding is added when capture is limited or legacy risks are detected.";
}

public sealed class RulePackService : IRulePackService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    static RulePackService()
    {
        JsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public async Task<IReadOnlyList<AccessibilityRule>> LoadRulesAsync(bool includeSection508, bool includeWcag22, IEnumerable<AccessibilityRule>? pdfOverlayRules = null, CancellationToken cancellationToken = default)
    {
        var rules = new List<AccessibilityRule>();
        rules.AddRange(await LoadRuleFileAsync("wcag-2.1-aa-static-rules.json", cancellationToken));

        if (includeSection508)
        {
            rules.AddRange(await LoadRuleFileAsync("section-508-static-rules.json", cancellationToken));
        }

        if (includeWcag22)
        {
            rules.AddRange(await LoadRuleFileAsync("wcag-2.2-static-rules.json", cancellationToken));
        }

        if (pdfOverlayRules is not null)
        {
            rules.AddRange(pdfOverlayRules.Select(r => r with { Source = string.IsNullOrWhiteSpace(r.Source) ? "pdf-overlay" : r.Source }));
        }

        return rules.GroupBy(r => r.RuleId, StringComparer.OrdinalIgnoreCase)
            .SelectMany(g => g.Count() == 1 ? g : g.Select(r => r with { ManualReviewRequired = true }))
            .ToList();
    }

    private static async Task<IReadOnlyList<AccessibilityRule>> LoadRuleFileAsync(string fileName, CancellationToken cancellationToken)
    {
        var baseDirectory = AppContext.BaseDirectory;
        var diskPath = Path.Combine(baseDirectory, "RulePacks", fileName);
        string json;

        if (File.Exists(diskPath))
        {
            json = await File.ReadAllTextAsync(diskPath, cancellationToken);
        }
        else
        {
            var assembly = typeof(RulePackService).Assembly;
            var resource = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
            if (resource is null)
            {
                throw new FileNotFoundException($"Rule pack '{fileName}' was not found as a copied file or embedded resource.");
            }

            await using var stream = assembly.GetManifestResourceStream(resource) ?? throw new FileNotFoundException(resource);
            using var reader = new StreamReader(stream);
            json = await reader.ReadToEndAsync(cancellationToken);
        }

        return JsonSerializer.Deserialize<List<AccessibilityRule>>(json, JsonOptions) ?? [];
    }
}

public sealed class StaticCheckEngine : IStaticCheckEngine
{
    private static readonly Regex TagRegex = new("<(?<tag>[a-zA-Z0-9:-]+)(?<attrs>[^>]*)>", RegexOptions.Compiled);
    private static readonly Regex IdRegex = new("\\bid\\s*=\\s*['\"](?<id>[^'\"]+)['\"]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AttrRegex = new("(?<name>[a-zA-Z_:][-a-zA-Z0-9_:.]*)\\s*=\\s*['\"](?<value>[^'\"]*)['\"]", RegexOptions.Compiled);
    private static readonly HashSet<string> VagueLinkText = new(StringComparer.OrdinalIgnoreCase) { "click here", "read more", "more", "here", "learn more" };

    public IReadOnlyList<AccessibilityFinding> Analyze(PageCapture page, IReadOnlyList<AccessibilityRule> rules)
    {
        var findings = new List<AccessibilityFinding>();
        var html = page.Html ?? "";

        AddIf(findings, page, rules, "WCAG21-2.4.2-PAGE-TITLE", !Regex.IsMatch(html, "<title>\\s*\\S+", RegexOptions.IgnoreCase), "missing-page-title", "Page is missing a non-empty title element.", "html > head > title");
        AddIf(findings, page, rules, "WCAG21-3.1.1-HTML-LANG", !Regex.IsMatch(html, "<html[^>]+lang\\s*=", RegexOptions.IgnoreCase), "missing-html-lang", "The html element does not declare a language.", "html");
        AddIf(findings, page, rules, "WCAG21-1.3.1-MAIN-LANDMARK", !Regex.IsMatch(html, "<main\\b|role\\s*=\\s*['\"]main['\"]", RegexOptions.IgnoreCase), "missing-main-landmark", "No main landmark was detected.", "body");

        var duplicateIds = IdRegex.Matches(html).Select(m => m.Groups["id"].Value).Where(v => !string.IsNullOrWhiteSpace(v))
            .GroupBy(v => v, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key);
        foreach (var id in duplicateIds)
        {
            findings.Add(Create(page, rules, "WCAG21-4.1.1-DUPLICATE-ID", "duplicate-id", $"Duplicate id '{id}' appears more than once.", $"#{id}"));
        }

        if (page.Headings.Count == 0)
        {
            findings.Add(Create(page, rules, "WCAG21-1.3.1-MISSING-H1", "missing-h1", "No h1 heading was detected.", "h1"));
        }
        else
        {
            AddIf(findings, page, rules, "WCAG21-1.3.1-MULTIPLE-H1", page.Headings.Count(h => h.Level == 1) > 1, "multiple-h1", "Multiple h1 headings were detected.", "h1");
            foreach (var heading in page.Headings.Where(h => string.IsNullOrWhiteSpace(h.Text)))
            {
                findings.Add(Create(page, rules, "WCAG21-1.3.1-EMPTY-HEADING", "empty-heading", "A heading element has no text.", heading.Selector));
            }

            var previous = page.Headings.First().Level;
            foreach (var heading in page.Headings.Skip(1))
            {
                if (heading.Level - previous > 1)
                {
                    findings.Add(Create(page, rules, "WCAG21-1.3.1-HEADING-ORDER", "heading-order-jump", $"Heading order jumps from h{previous} to h{heading.Level}.", heading.Selector));
                }
                previous = heading.Level;
            }
        }

        foreach (var image in page.Images)
        {
            if (string.IsNullOrWhiteSpace(image.Alt) && string.IsNullOrWhiteSpace(image.AccessibleName))
            {
                findings.Add(Create(page, rules, "WCAG21-1.1.1-IMG-ALT", "image-missing-alt", "Image has no alt text or accessible name.", image.Selector));
            }
            else if (LooksLikeFilename(image.Alt))
            {
                findings.Add(Create(page, rules, "WCAG21-1.1.1-IMG-ALT-SUSPICIOUS", "image-suspicious-alt", $"Image alt text looks like a filename: '{image.Alt}'.", image.Selector));
            }
        }

        foreach (var link in page.Links)
        {
            if (string.IsNullOrWhiteSpace(link.Text) && string.IsNullOrWhiteSpace(link.AccessibleName))
            {
                findings.Add(Create(page, rules, "WCAG21-2.4.4-LINK-NAME", "empty-link", "Link has no accessible name.", link.Selector));
            }
            else if (VagueLinkText.Contains((link.Text ?? link.AccessibleName ?? "").Trim()))
            {
                findings.Add(Create(page, rules, "WCAG21-2.4.4-VAGUE-LINK", "vague-link-text", $"Link text '{link.Text}' is vague without surrounding context.", link.Selector));
            }
        }

        var duplicateLinkTexts = page.Links.Where(l => !string.IsNullOrWhiteSpace(l.Text) && !string.IsNullOrWhiteSpace(l.Href))
            .GroupBy(l => l.Text.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Select(l => l.Href).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1);
        foreach (var group in duplicateLinkTexts)
        {
            findings.Add(Create(page, rules, "WCAG21-2.4.4-DUPLICATE-LINK-TEXT", "duplicate-link-text-different-url", $"Link text '{group.Key}' points to multiple URLs.", "a"));
        }

        foreach (var button in page.Buttons.Where(b => string.IsNullOrWhiteSpace(b.Text) && string.IsNullOrWhiteSpace(b.AccessibleName)))
        {
            findings.Add(Create(page, rules, "WCAG21-4.1.2-BUTTON-NAME", "button-missing-name", "Button has no accessible name.", button.Selector));
        }

        foreach (var input in page.Inputs.Where(i => !IsHiddenInput(i.Type) && string.IsNullOrWhiteSpace(i.Label)))
        {
            findings.Add(Create(page, rules, "WCAG21-1.3.1-FORM-LABEL", "form-control-missing-label", $"Input '{input.Name ?? input.Id ?? input.Selector}' has no associated label.", input.Selector));
        }

        foreach (var table in page.Tables)
        {
            AddIf(findings, page, rules, "WCAG21-1.3.1-TABLE-HEADER", !table.HasHeader, "table-missing-header", "Data table has no th header cells.", table.Selector);
            AddIf(findings, page, rules, "WCAG21-1.3.1-TABLE-CAPTION", !table.HasCaption, "table-missing-caption", "Table has no caption. Review whether a caption or surrounding context is needed.", table.Selector);
            AddIf(findings, page, rules, "WCAG21-1.3.1-COMPLEX-TABLE", table.IsComplex, "complex-table-manual-review", "Complex table detected; header relationships require manual review.", table.Selector, manualReview: true);
        }

        foreach (var iframe in page.Iframes.Where(i => string.IsNullOrWhiteSpace(i.AccessibleName) || IsVague(i.AccessibleName)))
        {
            findings.Add(Create(page, rules, "WCAG21-4.1.2-IFRAME-TITLE", "iframe-title-missing-or-vague", "Iframe title is missing, empty, or vague.", iframe.Selector));
        }

        AddAriaFindings(page, rules, findings, html);
        AddLegacyFindings(page, rules, findings);

        if (page.BrowserMode == BrowserMode.EdgeIeModeAssisted && (page.DomCaptureLimited || page.LegacyRisks.Count > 0))
        {
            findings.Add(Create(page, rules, "LEGACY-IE-MODE-MANUAL-REVIEW", "legacy-ie-mode-manual-review-required", ComplianceDisclaimers.IeMode, "document", manualReview: true));
        }

        return findings;
    }

    private static void AddAriaFindings(PageCapture page, IReadOnlyList<AccessibilityRule> rules, List<AccessibilityFinding> findings, string html)
    {
        var ids = IdRegex.Matches(html).Select(m => m.Groups["id"].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var aria in page.AriaAttributes)
        {
            if (aria.Attribute.Equals("aria-label", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(aria.Value))
            {
                findings.Add(Create(page, rules, "WCAG21-4.1.2-ARIA-LABEL-EMPTY", "aria-label-empty", "aria-label is present but empty.", aria.Selector));
            }

            if ((aria.Attribute.Equals("aria-labelledby", StringComparison.OrdinalIgnoreCase) || aria.Attribute.Equals("aria-describedby", StringComparison.OrdinalIgnoreCase)) && !string.IsNullOrWhiteSpace(aria.Value))
            {
                foreach (var id in aria.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!ids.Contains(id))
                    {
                        findings.Add(Create(page, rules, "WCAG21-4.1.2-ARIA-REFERENCE", "aria-reference-missing-id", $"{aria.Attribute} references missing id '{id}'.", aria.Selector));
                    }
                }
            }
        }

        foreach (Match element in TagRegex.Matches(html))
        {
            var attrs = ParseAttributes(element.Groups["attrs"].Value);
            if (attrs.TryGetValue("tabindex", out var tabindex) && int.TryParse(tabindex, out var tabIndexValue) && tabIndexValue > 0)
            {
                findings.Add(Create(page, rules, "WCAG21-2.4.3-POSITIVE-TABINDEX", "positive-tabindex", $"Positive tabindex '{tabIndexValue}' can create confusing focus order.", element.Groups["tag"].Value));
            }

            if (attrs.TryGetValue("aria-hidden", out var hidden) && hidden.Equals("true", StringComparison.OrdinalIgnoreCase) && IsFocusableTag(element.Groups["tag"].Value, attrs))
            {
                findings.Add(Create(page, rules, "WCAG21-4.1.2-ARIA-HIDDEN-FOCUSABLE", "aria-hidden-focusable", "Focusable element is hidden from assistive technology with aria-hidden=true.", element.Groups["tag"].Value));
            }

            if (attrs.TryGetValue("onclick", out _) && !IsNativeInteractive(element.Groups["tag"].Value))
            {
                findings.Add(Create(page, rules, "LEGACY-CLICKABLE-NONSEMANTIC", "clickable-nonsemantic-control", "Element has click behavior but is not a native interactive control.", element.Groups["tag"].Value, manualReview: true));
            }
        }
    }

    private static void AddLegacyFindings(PageCapture page, IReadOnlyList<AccessibilityRule> rules, List<AccessibilityFinding> findings)
    {
        foreach (var risk in page.LegacyRisks)
        {
            findings.Add(Create(page, rules, "LEGACY-IE-RISK", risk.RiskType, risk.Evidence, "document", manualReview: true) with { Severity = risk.Severity });
        }
    }

    private static void AddIf(List<AccessibilityFinding> findings, PageCapture page, IReadOnlyList<AccessibilityRule> rules, string ruleId, bool condition, string issueType, string evidence, string selector, bool manualReview = false)
    {
        if (condition)
        {
            findings.Add(Create(page, rules, ruleId, issueType, evidence, selector, manualReview));
        }
    }

    private static AccessibilityFinding Create(PageCapture page, IReadOnlyList<AccessibilityRule> rules, string ruleId, string issueType, string evidence, string selector, bool manualReview = false)
    {
        var rule = rules.FirstOrDefault(r => r.RuleId.Equals(ruleId, StringComparison.OrdinalIgnoreCase));
        return new AccessibilityFinding
        {
            PageUrl = page.SanitizedUrl,
            RuleId = ruleId,
            Standard = rule?.Standard ?? "Static accessibility rule",
            SuccessCriterion = rule?.SuccessCriterion ?? "",
            IssueType = issueType,
            Severity = rule?.SeverityDefault ?? Severity.Medium,
            Confidence = manualReview || rule?.ManualReviewRequired == true ? 0.65 : 0.95,
            Evidence = evidence,
            Selector = selector,
            NeedsManualReview = manualReview || rule?.ManualReviewRequired == true,
            RemediationHint = rule?.RemediationGuidance ?? "Review the markup and remediate according to the referenced accessibility standard.",
            Source = rule?.Source ?? "built-in-static",
            BrowserMode = page.BrowserMode
        };
    }

    private static bool LooksLikeFilename(string? value) =>
        !string.IsNullOrWhiteSpace(value) && Regex.IsMatch(value, "\\.(png|jpg|jpeg|gif|svg|webp)$", RegexOptions.IgnoreCase);

    private static bool IsVague(string? value) => string.IsNullOrWhiteSpace(value) || VagueLinkText.Contains(value.Trim()) || value.Trim().Length <= 2;
    private static bool IsHiddenInput(string type) => type.Equals("hidden", StringComparison.OrdinalIgnoreCase);
    private static bool IsNativeInteractive(string tag) => new[] { "a", "button", "input", "select", "textarea", "summary" }.Contains(tag, StringComparer.OrdinalIgnoreCase);
    private static bool IsFocusableTag(string tag, IReadOnlyDictionary<string, string> attrs) =>
        IsNativeInteractive(tag) || attrs.ContainsKey("tabindex");

    private static Dictionary<string, string> ParseAttributes(string attrs) =>
        AttrRegex.Matches(attrs).ToDictionary(m => m.Groups["name"].Value, m => m.Groups["value"].Value, StringComparer.OrdinalIgnoreCase);
}

public sealed class RuleMatcherService : IRuleMatcherService
{
    public IReadOnlyList<AccessibilityFinding> EnrichFindings(IEnumerable<AccessibilityFinding> findings, IReadOnlyList<AccessibilityRule> rules)
    {
        var pdfRules = rules.Where(r => r.Source.Contains("pdf", StringComparison.OrdinalIgnoreCase)).ToList();
        return findings.Select(f =>
        {
            var overlay = pdfRules.FirstOrDefault(r =>
                (!string.IsNullOrWhiteSpace(f.SuccessCriterion) && r.RawText.Contains(f.SuccessCriterion, StringComparison.OrdinalIgnoreCase)) ||
                r.Keywords.Any(k => f.IssueType.Contains(k, StringComparison.OrdinalIgnoreCase) || f.Evidence.Contains(k, StringComparison.OrdinalIgnoreCase)));

            if (overlay is null)
            {
                return f;
            }

            return f with
            {
                Confidence = Math.Min(f.Confidence, 0.85),
                NeedsManualReview = f.NeedsManualReview || overlay.ManualReviewRequired,
                RemediationHint = $"{f.RemediationHint} PDF overlay guidance: {overlay.RemediationGuidance}",
                Source = $"{f.Source}; pdf-overlay:{overlay.RuleId}"
            };
        }).ToList();
    }
}
