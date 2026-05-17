using System.Runtime.InteropServices;
using System.Text.Json;
using LegacyAccessibilityCrawler.Core;
using LegacyAccessibilityCrawler.Infrastructure;
using LegacyAccessibilityCrawler.Reporting;

var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "--help";

return command switch
{
    "version" => PrintVersion(),
    "extract-rules" => await ExtractRulesAsync(args.Skip(1).ToArray()),
    "crawl" => await CrawlAsync(args.Skip(1).ToArray()),
    "report" => await ReportAsync(args.Skip(1).ToArray()),
    "--help" or "-h" or "help" => PrintHelp(),
    _ => PrintHelp($"Unknown command '{command}'.")
};

static int PrintVersion()
{
    Console.WriteLine(ProductInfo.Name);
    Console.WriteLine($"Version: {ProductInfo.Version}");
    Console.WriteLine($"Build: {GetBuildConfiguration()}");
    Console.WriteLine($"Commit: {ProductInfo.Commit}");
    Console.WriteLine("Runtime: .NET 8");
    Console.WriteLine($"OS: {RuntimeInformation.OSDescription}");
    return 0;
}

static int PrintHelp(string? error = null)
{
    if (!string.IsNullOrWhiteSpace(error))
    {
        Console.Error.WriteLine(error);
    }

    Console.WriteLine("""
        legacy-accessibility-static-crawler

        Usage:
          legacy-a11y-crawler version
          legacy-a11y-crawler extract-rules --rules-pdf ./samples/sample-rules.pdf --output ./reports/rules
          legacy-a11y-crawler crawl --url https://example.gov --browser modern-edge --max-pages 25 --depth 2 --standard wcag21aa --output ./reports/example
          legacy-a11y-crawler crawl --url https://legacy.example.gov --browser edge-ie-mode-assisted --manual-session true --max-pages 10 --depth 1 --rules-pdf ./samples/sample-rules.pdf --output ./reports/legacy-ie
          legacy-a11y-crawler report --scan-results ./reports/example/scan-results.json --output ./reports/example/final

        Safety:
          Crawl only systems you are authorized to test. Credentials, cookies, and browser profiles are not stored by default.
        """);
    return string.IsNullOrWhiteSpace(error) ? 0 : 2;
}

static async Task<int> ExtractRulesAsync(string[] args)
{
    var options = Args.Parse(args);
    var pdf = options.Get("rules-pdf");
    var output = OutputPathSanitizer.ResolveOutputDirectory(options.Get("output", "reports/rules"), "reports");
    if (string.IsNullOrWhiteSpace(pdf))
    {
        return PrintHelp("--rules-pdf is required.");
    }

    var service = new PdfRulesLoaderService();
    var result = await service.ExtractAsync(pdf, output);
    Console.WriteLine($"Extracted {result.Rules.Count} PDF-derived guidance rule(s).");
    foreach (var warning in result.Warnings)
    {
        Console.WriteLine($"Warning: {warning}");
    }
    Console.WriteLine(result.NormalizedRulesPath);
    return 0;
}

static async Task<int> CrawlAsync(string[] args)
{
    var parsed = Args.Parse(args);
    var url = parsed.Get("url");
    if (string.IsNullOrWhiteSpace(url))
    {
        return PrintHelp("--url is required.");
    }

    var output = OutputPathSanitizer.ResolveOutputDirectory(parsed.Get("output", "reports/latest"), "reports");
    var browser = ParseBrowserMode(parsed.Get("browser", "modern-edge"));
    var allowedDomains = parsed.GetMany("allowed-domain");
    if (allowedDomains.Count == 0 && Uri.TryCreate(url, UriKind.Absolute, out var startUri))
    {
        allowedDomains = [startUri.Host];
    }

    var options = new CrawlerOptions
    {
        StartUrl = url,
        BrowserMode = browser,
        MaxPages = parsed.GetInt("max-pages", 25),
        CrawlDepth = parsed.GetInt("depth", 2),
        OutputDirectory = output,
        ManualSession = parsed.GetBool("manual-session"),
        Headless = !parsed.GetBool("show-browser"),
        AcceptInsecureCertificates = parsed.GetBool("accept-insecure-certificates"),
        EnableWcag22EnhancedRules = string.Equals(parsed.Get("standard"), "wcag22", StringComparison.OrdinalIgnoreCase),
        EnableSection508Rules = !string.Equals(parsed.Get("section508"), "false", StringComparison.OrdinalIgnoreCase),
        RulesPdfPath = parsed.Get("rules-pdf"),
        AllowedDomains = allowedDomains
    };
    options = CrawlRequestValidator.ValidateAndNormalize(options, options.AllowedDomains, "reports", allowPrivateNetworkTargets: true);

    var pdfRules = new List<AccessibilityRule>();
    if (!string.IsNullOrWhiteSpace(options.RulesPdfPath))
    {
        var extraction = await new PdfRulesLoaderService().ExtractAsync(options.RulesPdfPath, Path.Combine(output, "rules"));
        pdfRules.AddRange(extraction.Rules);
    }

    var rulePackService = new RulePackService();
    var rules = await rulePackService.LoadRulesAsync(options.EnableSection508Rules, options.EnableWcag22EnhancedRules, pdfRules);
    var crawler = new SeleniumCrawlerService();
    var pages = await crawler.CrawlAsync(options);
    var engine = new StaticCheckEngine();
    var matcher = new RuleMatcherService();
    var findings = matcher.EnrichFindings(pages.SelectMany(p => engine.Analyze(p, rules)), rules);
    var result = new ScanResult
    {
        Options = options,
        Pages = pages,
        Findings = findings,
        RulesUsed = rules,
        Disclaimer = options.BrowserMode == BrowserMode.EdgeIeModeAssisted
            ? $"{ComplianceDisclaimers.Standard} {ComplianceDisclaimers.IeMode}"
            : ComplianceDisclaimers.Standard
    };

    Directory.CreateDirectory(output);
    var scanPath = Path.Combine(output, "scan-results.json");
    await File.WriteAllTextAsync(scanPath, JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    var manifest = await new ReportGenerator().GenerateAsync(result, output);
    Console.WriteLine($"Crawled {pages.Count} page(s), produced {findings.Count} finding(s).");
    Console.WriteLine($"Scan results: {scanPath}");
    Console.WriteLine($"HTML report: {manifest.HtmlPath}");
    return 0;
}

static async Task<int> ReportAsync(string[] args)
{
    var parsed = Args.Parse(args);
    var scanResults = parsed.Get("scan-results");
    var output = OutputPathSanitizer.ResolveOutputDirectory(parsed.Get("output", "reports/final"), "reports");
    if (string.IsNullOrWhiteSpace(scanResults) || !File.Exists(scanResults))
    {
        return PrintHelp("--scan-results is required and must exist.");
    }

    var result = JsonSerializer.Deserialize<ScanResult>(await File.ReadAllTextAsync(scanResults), new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true })
        ?? throw new InvalidOperationException("Unable to deserialize scan results.");
    var manifest = await new ReportGenerator().GenerateAsync(result, output);
    Console.WriteLine($"Reports written to {output}");
    Console.WriteLine($"ADO backlog CSV: {manifest.AdoCsvPath}");
    return 0;
}

static BrowserMode ParseBrowserMode(string value) => value.ToLowerInvariant() switch
{
    "chrome" => BrowserMode.Chrome,
    "edge-ie-mode-assisted" => BrowserMode.EdgeIeModeAssisted,
    _ => BrowserMode.ModernEdge
};

static string GetBuildConfiguration()
{
#if DEBUG
    return "Debug";
#else
    return "Release";
#endif
}

internal sealed class Args
{
    private readonly Dictionary<string, string> _values;
    private Args(Dictionary<string, string> values) => _values = values;

    public static Args Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }
            var key = args[i][2..];
            var value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
            values[key] = value;
        }
        return new Args(values);
    }

    public string Get(string key, string defaultValue = "") => _values.TryGetValue(key, out var value) ? value : defaultValue;
    public IReadOnlyList<string> GetMany(string key) => Get(key).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    public int GetInt(string key, int defaultValue) => int.TryParse(Get(key), out var value) ? value : defaultValue;
    public bool GetBool(string key) => bool.TryParse(Get(key), out var value) && value;
}
