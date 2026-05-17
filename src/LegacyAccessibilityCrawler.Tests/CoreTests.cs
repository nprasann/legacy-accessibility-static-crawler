using LegacyAccessibilityCrawler.Core;
using LegacyAccessibilityCrawler.Infrastructure;
using LegacyAccessibilityCrawler.Reporting;
using System.Security.Cryptography;
using System.Text;

namespace LegacyAccessibilityCrawler.Tests;

public sealed class CoreTests
{
    [Fact]
    public async Task BuiltInRulePackLoading_IncludesWcagAndSection508()
    {
        var rules = await new RulePackService().LoadRulesAsync(includeSection508: true, includeWcag22: false);

        Assert.Contains(rules, r => r.RuleId == "WCAG21-1.1.1-IMG-ALT");
        Assert.Contains(rules, r => r.Standard.Contains("Section 508"));
    }

    [Fact]
    public async Task Wcag22RulePack_IsOptional()
    {
        var rules = await new RulePackService().LoadRulesAsync(includeSection508: false, includeWcag22: true);

        Assert.Contains(rules, r => r.Standard == "WCAG 2.2");
    }

    [Fact]
    public async Task StaticChecks_DetectMissingAltFormLabelHeadingOrderIframeAndAriaReference()
    {
        var rules = await new RulePackService().LoadRulesAsync(includeSection508: true, includeWcag22: false);
        var page = new PageCapture
        {
            SanitizedUrl = "https://example.test",
            BrowserMode = BrowserMode.ModernEdge,
            Html = """
                <html><head><title>Example</title></head><body>
                <h1>Title</h1><h3>Jump</h3>
                <img src="chart.png">
                <input id="email" name="email">
                <iframe src="x.html"></iframe>
                <button aria-describedby="missing"></button>
                </body></html>
                """,
            Headings = [new(1, "Title", "h1"), new(3, "Jump", "h3")],
            Images = [new("img", null, "chart.png", null)],
            Inputs = [new("input#email", "text", "email", "email", null, false)],
            Iframes = [new("iframe", "", null, "x.html")],
            AriaAttributes = [new("button", "aria-describedby", "missing")]
        };

        var findings = new StaticCheckEngine().Analyze(page, rules);

        Assert.Contains(findings, f => f.IssueType == "image-missing-alt");
        Assert.Contains(findings, f => f.IssueType == "form-control-missing-label");
        Assert.Contains(findings, f => f.IssueType == "heading-order-jump");
        Assert.Contains(findings, f => f.IssueType == "iframe-title-missing-or-vague");
        Assert.Contains(findings, f => f.IssueType == "aria-reference-missing-id");
    }

    [Fact]
    public async Task RuleMatcher_AppliesPdfOverlayWithoutInventingEvidence()
    {
        var builtIn = await new RulePackService().LoadRulesAsync(includeSection508: false, includeWcag22: false);
        var pdfRule = new AccessibilityRule
        {
            RuleId = "PDF-1",
            Source = "pdf-overlay",
            SuccessCriterion = "1.3.1",
            Keywords = ["label"],
            ManualReviewRequired = true,
            RemediationGuidance = "Agency requires visible labels."
        };
        var rules = builtIn.Concat([pdfRule]).ToList();
        var finding = new AccessibilityFinding
        {
            PageUrl = "https://example.test",
            RuleId = "WCAG21-1.3.1-FORM-LABEL",
            SuccessCriterion = "1.3.1",
            IssueType = "form-control-missing-label",
            Evidence = "Input has no associated label."
        };

        var enriched = new RuleMatcherService().EnrichFindings([finding], rules).Single();

        Assert.Contains("pdf-overlay:PDF-1", enriched.Source);
        Assert.Equal(finding.Evidence, enriched.Evidence);
        Assert.True(enriched.NeedsManualReview);
    }

    [Fact]
    public async Task ReportGeneration_WritesJsonMarkdownCsvAndAdo()
    {
        var output = Path.Combine(Path.GetTempPath(), "legacy-a11y-report-tests", Guid.NewGuid().ToString("N"));
        var result = new ScanResult
        {
            Options = new CrawlerOptions { OutputDirectory = output },
            Pages = [new PageCapture { SanitizedUrl = "https://example.test", BrowserMode = BrowserMode.ModernEdge }],
            Findings =
            [
                new AccessibilityFinding
                {
                    PageUrl = "https://example.test",
                    RuleId = "WCAG21-1.1.1-IMG-ALT",
                    Standard = "WCAG 2.1",
                    SuccessCriterion = "1.1.1",
                    IssueType = "image-missing-alt",
                    Severity = Severity.High,
                    Evidence = "Image missing alt.",
                    BrowserMode = BrowserMode.ModernEdge
                }
            ]
        };

        var manifest = await new ReportGenerator().GenerateAsync(result, output);

        Assert.True(File.Exists(manifest.JsonPath));
        Assert.True(File.Exists(manifest.MarkdownPath));
        Assert.True(File.Exists(manifest.FindingsCsvPath));
        Assert.True(File.Exists(manifest.AdoCsvPath));
    }

    [Fact]
    public async Task ReportGeneration_EncodesHtmlAndNeutralizesCsvFormulas()
    {
        var output = Path.Combine(Path.GetTempPath(), "legacy-a11y-report-security-tests", Guid.NewGuid().ToString("N"));
        var payload = "\"><script>alert(1)</script>";
        var result = new ScanResult
        {
            Options = new CrawlerOptions { OutputDirectory = output },
            Pages = [new PageCapture { SanitizedUrl = payload, BrowserMode = BrowserMode.ModernEdge }],
            Findings =
            [
                new AccessibilityFinding
                {
                    PageUrl = "=HYPERLINK(\"https://attacker.test\")",
                    RuleId = payload,
                    Standard = "WCAG 2.1",
                    SuccessCriterion = "1.1.1",
                    IssueType = payload,
                    Severity = Severity.High,
                    Evidence = payload,
                    BrowserMode = BrowserMode.ModernEdge
                }
            ]
        };

        var manifest = await new ReportGenerator().GenerateAsync(result, output);
        var html = await File.ReadAllTextAsync(manifest.HtmlPath);
        var csv = await File.ReadAllTextAsync(manifest.FindingsCsvPath);

        Assert.DoesNotContain(payload, html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.Contains("'=HYPERLINK", csv);
    }

    [Fact]
    public void IeModeDisclaimer_IsExplicit()
    {
        Assert.Contains("automation limitations", ComplianceDisclaimers.IeMode, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Manual Review Required", ComplianceDisclaimers.IeMode, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LlmService_IsDisabledByDefault()
    {
        ILlmReviewService service = new DisabledLlmReviewService();

        Assert.False(service.IsEnabled);
        Assert.Contains("disabled", await service.ExplainFindingAsync(new AccessibilityFinding()));
    }

    [Fact]
    public async Task ManualFindingsImport_ReadsCsv()
    {
        var csvPath = Path.Combine(Path.GetTempPath(), $"manual-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(csvPath, """
            PageUrl,PageTitle,BrowserMode,AssistiveTechnology,TestArea,IssueSummary,ObservedBehavior,ExpectedBehavior,RuleReference,Severity,ScreenshotPath,RemediationNotes
            https://example.test,Example,EdgeIeModeAssisted,NVDA,Keyboard,Trap,Observed,Expected,WCAG 2.1 2.1.2,High,shot.png,Fix widget
            """);

        var imported = await new ManualFindingsImporter().ImportAsync(csvPath);

        Assert.Single(imported);
        Assert.Equal("Trap", imported[0].IssueSummary);
    }

    [Fact]
    public void OutputPathSanitizer_RejectsTraversalOutsideBaseDirectory()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "legacy-a11y-output-base");

        Assert.Throws<ArgumentException>(() => OutputPathSanitizer.ResolveOutputDirectory("../outside", baseDirectory));
        Assert.Throws<ArgumentException>(() => OutputPathSanitizer.ResolveOutputDirectory(Path.GetTempPath(), baseDirectory));
    }

    [Fact]
    public void CrawlRequestValidator_RequiresConfiguredAllowedDomain()
    {
        Assert.Throws<ArgumentException>(() => CrawlRequestValidator.ValidateStartUrl("https://example.gov", []));
        Assert.Throws<ArgumentException>(() => CrawlRequestValidator.ValidateStartUrl("https://evil.test", ["example.gov"]));
        Assert.Throws<ArgumentException>(() => CrawlRequestValidator.ValidateStartUrl("https://localhost", ["localhost"]));

        var uri = CrawlRequestValidator.ValidateStartUrl("https://sub.example.gov/path", ["*.example.gov"]);

        Assert.Equal("sub.example.gov", uri.Host);
    }

    [Fact]
    public void ApiKeySecurity_RequiresExactConfiguredKey()
    {
        var material = new ApiKeyMaterial([SHA256.HashData(Encoding.UTF8.GetBytes("local-key"))]);

        Assert.True(ApiKeySecurity.IsValid("local-key", material));
        Assert.False(ApiKeySecurity.IsValid("wrong", material));
        Assert.False(ApiKeySecurity.IsValid("", material));
    }

    [Fact]
    public void FileInputValidator_RestrictsExtensionSizeAndBaseDirectory()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), "legacy-a11y-input-base", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(baseDirectory);
        var pdfPath = Path.Combine(baseDirectory, "rules.pdf");
        File.WriteAllText(pdfPath, "%PDF-test");

        var resolved = FileInputValidator.ResolveExistingFile("rules.pdf", baseDirectory, ".pdf", 1024);

        Assert.Equal(Path.GetFullPath(pdfPath), resolved);
        Assert.Throws<ArgumentException>(() => FileInputValidator.ResolveExistingFile("../rules.pdf", baseDirectory, ".pdf", 1024));
        Assert.Throws<ArgumentException>(() => FileInputValidator.ResolveExistingFile("rules.pdf", baseDirectory, ".csv", 1024));
        Assert.Throws<ArgumentException>(() => FileInputValidator.ResolveExistingFile("rules.pdf", baseDirectory, ".pdf", 2));
    }

    [Fact(Skip = "Requires a real PDF fixture and PdfPig restore; sample placeholder documents the supported workflow.")]
    public async Task PdfExtraction_NormalizesRules()
    {
        var result = await new PdfRulesLoaderService().ExtractAsync("samples/sample-rules.pdf", Path.Combine(Path.GetTempPath(), "pdf-rules"));
        Assert.NotNull(result);
    }
}
