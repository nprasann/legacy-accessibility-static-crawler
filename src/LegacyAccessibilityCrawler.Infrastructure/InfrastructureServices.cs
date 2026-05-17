using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CsvHelper;
using HtmlAgilityPack;
using LegacyAccessibilityCrawler.Core;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Edge;
using UglyToad.PdfPig;

namespace LegacyAccessibilityCrawler.Infrastructure;

public sealed class SeleniumCrawlerService : ICrawlerService
{
    public async Task<IReadOnlyList<PageCapture>> CrawlAsync(CrawlerOptions options, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.StartUrl))
        {
            throw new ArgumentException("StartUrl is required.", nameof(options));
        }

        Directory.CreateDirectory(options.OutputDirectory);
        Directory.CreateDirectory(Path.Combine(options.OutputDirectory, "raw-html"));
        Directory.CreateDirectory(Path.Combine(options.OutputDirectory, "screenshots"));

        using var driver = CreateDriver(options);
        var queue = new Queue<(Uri Url, int Depth)>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var captures = new List<PageCapture>();
        var start = new Uri(options.StartUrl);
        queue.Enqueue((start, 0));

        if (options.ManualSession || options.BrowserMode == BrowserMode.EdgeIeModeAssisted)
        {
            driver.Navigate().GoToUrl(start);
            Console.WriteLine("Manual session mode: complete login/navigation in the browser, then press Enter to continue. Credentials are never stored.");
            Console.ReadLine();
        }

        while (queue.Count > 0 && captures.Count < options.MaxPages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (url, depth) = queue.Dequeue();
            var sanitized = SanitizeUrl(url, options.RedactQueryStrings);
            if (!visited.Add(sanitized))
            {
                continue;
            }

            driver.Navigate().GoToUrl(url);
            if (options.DelaySeconds > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(options.DelaySeconds), cancellationToken);
            }

            var html = SafeGetPageSource(driver);
            var capture = BuildCapture(driver, options, url, sanitized, html, captures.Count + 1);
            captures.Add(capture);

            if (depth >= options.CrawlDepth || options.BrowserMode == BrowserMode.EdgeIeModeAssisted)
            {
                continue;
            }

            foreach (var link in capture.Links.Select(l => l.Href).Where(h => !string.IsNullOrWhiteSpace(h)))
            {
                if (!Uri.TryCreate(url, link, out var next) || next.Scheme is not ("http" or "https"))
                {
                    continue;
                }

                if (options.SameDomainOnly && !string.Equals(next.Host, start.Host, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (options.AllowedDomains.Count > 0 && !options.AllowedDomains.Contains(next.Host, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                queue.Enqueue((next, depth + 1));
            }
        }

        return captures;
    }

    private static IWebDriver CreateDriver(CrawlerOptions options)
    {
        return options.BrowserMode switch
        {
            BrowserMode.Chrome => new ChromeDriver(CreateChromeOptions(options)),
            BrowserMode.EdgeIeModeAssisted => CreateIeModeAssistedEdgeDriver(),
            _ => new EdgeDriver(CreateEdgeOptions(options))
        };
    }

    private static ChromeOptions CreateChromeOptions(CrawlerOptions options)
    {
        var chromeOptions = new ChromeOptions { AcceptInsecureCertificates = options.AcceptInsecureCertificates };
        if (options.Headless && !options.ManualSession)
        {
            chromeOptions.AddArgument("--headless=new");
            chromeOptions.AddArgument("--window-size=1440,1200");
        }

        return chromeOptions;
    }

    private static EdgeOptions CreateEdgeOptions(CrawlerOptions options)
    {
        var edgeOptions = new EdgeOptions { AcceptInsecureCertificates = options.AcceptInsecureCertificates };
        if (options.Headless && !options.ManualSession)
        {
            edgeOptions.AddArgument("--headless=new");
            edgeOptions.AddArgument("--window-size=1440,1200");
        }

        return edgeOptions;
    }

    private static IWebDriver CreateIeModeAssistedEdgeDriver()
    {
        var edgeOptions = new EdgeOptions { AcceptInsecureCertificates = false };
        // IE mode requires enterprise Edge security zone and site-list policy configuration outside Selenium.
        edgeOptions.AddArgument("--ie-mode-test");
        return new EdgeDriver(edgeOptions);
    }

    private static string SafeGetPageSource(IWebDriver driver)
    {
        try
        {
            return driver.PageSource ?? "";
        }
        catch (WebDriverException)
        {
            return "";
        }
    }

    private static PageCapture BuildCapture(IWebDriver driver, CrawlerOptions options, Uri url, string sanitizedUrl, string html, int index)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);

        var htmlPath = options.CaptureHtml ? Path.Combine(options.OutputDirectory, "raw-html", $"{index:000}.html") : null;
        if (htmlPath is not null)
        {
            File.WriteAllText(htmlPath, html);
        }

        string? screenshotPath = null;
        if (options.CaptureScreenshots && driver is ITakesScreenshot screenshotDriver)
        {
            screenshotPath = Path.Combine(options.OutputDirectory, "screenshots", $"{index:000}.png");
            screenshotDriver.GetScreenshot().SaveAsFile(screenshotPath);
        }

        return new PageCapture
        {
            Url = url.ToString(),
            SanitizedUrl = sanitizedUrl,
            BrowserMode = options.BrowserMode,
            Title = document.DocumentNode.SelectSingleNode("//title")?.InnerText.Trim() ?? driver.Title ?? "",
            Html = html,
            HtmlPath = htmlPath,
            ScreenshotPath = screenshotPath,
            Headings = ExtractHeadings(document),
            Links = ExtractLinks(document),
            Buttons = ExtractButtons(document),
            Inputs = ExtractInputs(document),
            Labels = ExtractLabels(document),
            Images = ExtractImages(document),
            Tables = ExtractTables(document),
            Iframes = ExtractIframes(document),
            AriaAttributes = ExtractAria(document),
            FocusableElements = ExtractFocusable(document),
            VisibleTextSummary = HtmlEntity.DeEntitize(document.DocumentNode.InnerText).Trim().CollapseWhitespace().Truncate(1000),
            LegacyRisks = ExtractLegacyRisks(document, html),
            DomCaptureLimited = string.IsNullOrWhiteSpace(html) || options.BrowserMode == BrowserMode.EdgeIeModeAssisted && html.Length < 500
        };
    }

    private static IReadOnlyList<HeadingEvidence> ExtractHeadings(HtmlDocument document) =>
        document.DocumentNode.SelectNodes("//h1|//h2|//h3|//h4|//h5|//h6")?
            .Select((n, i) => new HeadingEvidence(int.Parse(n.Name[1].ToString(), CultureInfo.InvariantCulture), n.InnerText.Trim(), $"{n.Name}:nth-of-type({i + 1})"))
            .ToList() ?? [];

    private static IReadOnlyList<ElementEvidence> ExtractLinks(HtmlDocument document) =>
        document.DocumentNode.SelectNodes("//a")?
            .Select((n, i) => new ElementEvidence($"a:nth-of-type({i + 1})", n.InnerText.Trim(), AccessibleName(n), n.GetAttributeValue("href", null)))
            .ToList() ?? [];

    private static IReadOnlyList<ElementEvidence> ExtractButtons(HtmlDocument document)
    {
        var buttonNodes = document.DocumentNode.SelectNodes("//button|//input[@type='button' or @type='submit' or @type='reset' or @type='image']") ?? Enumerable.Empty<HtmlNode>();
        return buttonNodes.Select((n, i) => new ElementEvidence($"{n.Name}:nth-of-type({i + 1})", n.InnerText.Trim(), AccessibleName(n), Type: n.GetAttributeValue("type", null))).ToList();
    }

    private static IReadOnlyList<InputEvidence> ExtractInputs(HtmlDocument document)
    {
        var labelByFor = (document.DocumentNode.SelectNodes("//label[@for]") ?? Enumerable.Empty<HtmlNode>())
            .GroupBy(n => n.GetAttributeValue("for", ""), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().InnerText.Trim(), StringComparer.OrdinalIgnoreCase);

        return (document.DocumentNode.SelectNodes("//input|//select|//textarea") ?? Enumerable.Empty<HtmlNode>())
            .Select((n, i) =>
            {
                var id = n.GetAttributeValue("id", null);
                var implicitLabel = n.Ancestors("label").FirstOrDefault()?.InnerText.Trim();
                var explicitLabel = id is not null && labelByFor.TryGetValue(id, out var text) ? text : null;
                var aria = AccessibleName(n);
                return new InputEvidence(
                    $"{n.Name}:nth-of-type({i + 1})",
                    n.GetAttributeValue("type", n.Name),
                    id,
                    n.GetAttributeValue("name", null),
                    explicitLabel ?? implicitLabel ?? aria,
                    n.Attributes.Contains("required") || n.GetAttributeValue("aria-required", "false").Equals("true", StringComparison.OrdinalIgnoreCase));
            }).ToList();
    }

    private static IReadOnlyList<ElementEvidence> ExtractLabels(HtmlDocument document) =>
        (document.DocumentNode.SelectNodes("//label") ?? Enumerable.Empty<HtmlNode>()).Select((n, i) => new ElementEvidence($"label:nth-of-type({i + 1})", n.InnerText.Trim(), n.GetAttributeValue("for", null))).ToList();

    private static IReadOnlyList<ImageEvidence> ExtractImages(HtmlDocument document) =>
        (document.DocumentNode.SelectNodes("//img|//input[@type='image']|//*[local-name()='svg']") ?? Enumerable.Empty<HtmlNode>())
            .Select((n, i) => new ImageEvidence($"{n.Name}:nth-of-type({i + 1})", n.GetAttributeValue("alt", null), n.GetAttributeValue("src", null), AccessibleName(n)))
            .ToList();

    private static IReadOnlyList<TableEvidence> ExtractTables(HtmlDocument document) =>
        (document.DocumentNode.SelectNodes("//table") ?? Enumerable.Empty<HtmlNode>())
            .Select((n, i) => new TableEvidence($"table:nth-of-type({i + 1})", n.SelectSingleNode(".//th") is not null, n.SelectSingleNode(".//caption") is not null, n.SelectNodes(".//th[@colspan or @rowspan]")?.Count > 0))
            .ToList();

    private static IReadOnlyList<ElementEvidence> ExtractIframes(HtmlDocument document) =>
        (document.DocumentNode.SelectNodes("//iframe") ?? Enumerable.Empty<HtmlNode>()).Select((n, i) => new ElementEvidence($"iframe:nth-of-type({i + 1})", "", n.GetAttributeValue("title", null), n.GetAttributeValue("src", null))).ToList();

    private static IReadOnlyList<AriaEvidence> ExtractAria(HtmlDocument document) =>
        document.DocumentNode.Descendants()
            .SelectMany((n, i) => n.Attributes.Where(a => a.Name.StartsWith("aria-", StringComparison.OrdinalIgnoreCase))
                .Select(a => new AriaEvidence($"{n.Name}:nth-of-type({i + 1})", a.Name, a.Value)))
            .ToList();

    private static IReadOnlyList<ElementEvidence> ExtractFocusable(HtmlDocument document) =>
        (document.DocumentNode.SelectNodes("//a[@href]|//button|//input|//select|//textarea|//*[@tabindex]") ?? Enumerable.Empty<HtmlNode>())
            .Select((n, i) => new ElementEvidence($"{n.Name}:nth-of-type({i + 1})", n.InnerText.Trim(), AccessibleName(n), n.GetAttributeValue("href", null), n.GetAttributeValue("type", null)))
            .ToList();

    private static IReadOnlyList<LegacyRisk> ExtractLegacyRisks(HtmlDocument document, string html)
    {
        var risks = new List<LegacyRisk>();
        if (document.DocumentNode.SelectSingleNode("//object|//embed|//applet") is not null)
        {
            risks.Add(new LegacyRisk("activex-object-embed-applet", "ActiveX/object/embed/applet-style content was detected.", Severity.High));
        }
        if (document.DocumentNode.SelectSingleNode("//frameset|//frame") is not null)
        {
            risks.Add(new LegacyRisk("frameset-frame-usage", "frameset/frame markup was detected.", Severity.Medium));
        }
        if (Regex.IsMatch(html, "X-UA-Compatible|documentMode|IE=", RegexOptions.IgnoreCase))
        {
            risks.Add(new LegacyRisk("old-document-mode", "Legacy document mode or IE compatibility metadata was detected.", Severity.Medium));
        }
        if (Regex.IsMatch(html, "href\\s*=\\s*['\"]javascript:", RegexOptions.IgnoreCase))
        {
            risks.Add(new LegacyRisk("javascript-link", "javascript: link navigation was detected.", Severity.Medium));
        }
        return risks;
    }

    private static string? AccessibleName(HtmlNode node) =>
        FirstNonEmpty(node.GetAttributeValue("aria-label", null), node.GetAttributeValue("title", null), node.GetAttributeValue("alt", null), node.GetAttributeValue("value", null));

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static string SanitizeUrl(Uri url, bool redactQueryStrings)
    {
        if (!redactQueryStrings || string.IsNullOrEmpty(url.Query))
        {
            return url.ToString();
        }

        var builder = new UriBuilder(url) { Query = "" };
        return builder.Uri.ToString();
    }
}

public sealed class PdfRulesLoaderService : IPdfRulesLoaderService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task<PdfExtractionResult> ExtractAsync(string pdfPath, string outputDirectory, CancellationToken cancellationToken = default)
    {
        var pdfInfo = new FileInfo(pdfPath);
        if (!pdfInfo.Exists)
        {
            throw new FileNotFoundException("Rules PDF was not found.", pdfPath);
        }

        if (!string.Equals(pdfInfo.Extension, ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Rules file must be a PDF.", nameof(pdfPath));
        }

        if (pdfInfo.Length > 25 * 1024 * 1024)
        {
            throw new ArgumentException("Rules PDF exceeds the maximum supported file size.", nameof(pdfPath));
        }

        Directory.CreateDirectory(outputDirectory);
        var pages = new List<(int Page, string Text)>();
        using (var document = PdfDocument.Open(pdfPath))
        {
            foreach (var page in document.GetPages())
            {
                pages.Add((page.Number, page.Text));
            }
        }

        var rawText = string.Join(Environment.NewLine + Environment.NewLine, pages.Select(p => $"# Page {p.Page}{Environment.NewLine}{p.Text}"));
        var chunks = SplitIntoChunks(pages);
        var rules = chunks.Select(NormalizeRule).ToList();
        var warnings = new List<string>();
        if (rules.Count == 0 || rules.Count(r => !string.IsNullOrWhiteSpace(r.SuccessCriterion)) < Math.Max(1, rules.Count / 4))
        {
            warnings.Add("PDF extraction confidence is low. Review raw text and normalized rule chunks manually.");
        }

        var rawPath = Path.Combine(outputDirectory, "rules-raw-text.txt");
        var chunksPath = Path.Combine(outputDirectory, "rules-chunks.json");
        var rulesPath = Path.Combine(outputDirectory, "rules-normalized.json");
        await File.WriteAllTextAsync(rawPath, rawText, cancellationToken);
        await File.WriteAllTextAsync(chunksPath, JsonSerializer.Serialize(chunks, JsonOptions), cancellationToken);
        await File.WriteAllTextAsync(rulesPath, JsonSerializer.Serialize(rules, JsonOptions), cancellationToken);

        return new PdfExtractionResult(pdfPath, rawPath, chunksPath, rulesPath, rules, warnings);
    }

    private static IReadOnlyList<PdfChunk> SplitIntoChunks(IEnumerable<(int Page, string Text)> pages)
    {
        var chunks = new List<PdfChunk>();
        foreach (var page in pages)
        {
            var parts = Regex.Split(page.Text, "(?=\\n\\s*(\\d+(\\.\\d+)+|WCAG|Section 508|ADA)\\b)", RegexOptions.IgnoreCase)
                .Select(p => p.Trim()).Where(p => p.Length > 80);
            foreach (var part in parts)
            {
                chunks.Add(new PdfChunk(page.Page, part[..Math.Min(part.Length, 80)], part));
            }
        }
        return chunks;
    }

    private static AccessibilityRule NormalizeRule(PdfChunk chunk)
    {
        var wcag = Regex.Match(chunk.Text, "(?<sc>[1-4]\\.[0-9]+\\.[0-9]+)");
        var severity = Regex.Match(chunk.Text, "\\b(Critical|High|Medium|Low|Info)\\b", RegexOptions.IgnoreCase);
        return new AccessibilityRule
        {
            RuleId = $"PDF-{chunk.Page}-{Math.Abs(chunk.Text.GetHashCode()):X}",
            Standard = wcag.Success ? "PDF overlay / WCAG reference" : "PDF overlay",
            SuccessCriterion = wcag.Success ? wcag.Groups["sc"].Value : "",
            Level = "",
            Title = chunk.Heading.CollapseWhitespace().Truncate(120),
            Description = chunk.Text.CollapseWhitespace().Truncate(500),
            Source = "pdf-overlay",
            SourcePage = chunk.Page,
            SeverityDefault = severity.Success && Enum.TryParse<Severity>(severity.Value, true, out var parsed) ? parsed : Severity.Medium,
            Keywords = ExtractKeywords(chunk.Text),
            AppliesTo = [],
            TestMethod = ExtractSentence(chunk.Text, "test") ?? "Review PDF-derived agency guidance.",
            StaticCheckSupported = false,
            ManualReviewRequired = true,
            RemediationGuidance = ExtractSentence(chunk.Text, "remediat") ?? ExtractSentence(chunk.Text, "fix") ?? "Review the source PDF guidance and apply agency remediation direction.",
            References = ExtractReferences(chunk.Text),
            RawText = chunk.Text
        };
    }

    private static IReadOnlyList<string> ExtractKeywords(string text) =>
        Regex.Matches(text.ToLowerInvariant(), "\\b(alt|image|heading|label|form|keyboard|focus|aria|table|caption|contrast|iframe|link|button|error|508|ada|wcag)\\b")
            .Select(m => m.Value).Distinct().ToList();

    private static string? ExtractSentence(string text, string contains) =>
        Regex.Split(text, "[\\.!?]\\s+").FirstOrDefault(s => s.Contains(contains, StringComparison.OrdinalIgnoreCase))?.Trim();

    private static IReadOnlyList<string> ExtractReferences(string text) =>
        Regex.Matches(text, "https?://\\S+|WCAG\\s*[0-9.]+|Section\\s*508|ADA", RegexOptions.IgnoreCase)
            .Select(m => m.Value.TrimEnd('.', ',', ';')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private sealed record PdfChunk(int Page, string Heading, string Text);
}

public sealed class ManualFindingsImporter : IManualFindingsImporter
{
    public async Task<IReadOnlyList<ManualFinding>> ImportAsync(string csvPath, CancellationToken cancellationToken = default)
    {
        var csvInfo = new FileInfo(csvPath);
        if (!csvInfo.Exists)
        {
            throw new FileNotFoundException("Manual findings CSV was not found.", csvPath);
        }

        if (!string.Equals(csvInfo.Extension, ".csv", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Manual findings import must be a CSV file.", nameof(csvPath));
        }

        if (csvInfo.Length > 5 * 1024 * 1024)
        {
            throw new ArgumentException("Manual findings CSV exceeds the maximum supported file size.", nameof(csvPath));
        }

        await using var stream = File.OpenRead(csvPath);
        using var reader = new StreamReader(stream);
        using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
        return csv.GetRecords<ManualFinding>().ToList();
    }
}

public static class StringExtensions
{
    public static string CollapseWhitespace(this string value) => Regex.Replace(value ?? "", "\\s+", " ").Trim();
    public static string Truncate(this string value, int length) => value.Length <= length ? value : value[..length];
}
