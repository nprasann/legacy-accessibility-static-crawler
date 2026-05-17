using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using LegacyAccessibilityCrawler.Core;
using LegacyAccessibilityCrawler.Infrastructure;
using LegacyAccessibilityCrawler.Reporting;
using Microsoft.OpenApi.Models;
using Serilog;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog((context, configuration) => configuration.ReadFrom.Configuration(context.Configuration).WriteTo.Console());
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.AddSecurityDefinition("ApiKey", new OpenApiSecurityScheme
    {
        Description = "API key required for /api routes. Use the x-api-key header.",
        Name = "x-api-key",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey
    });
    options.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        [new OpenApiSecurityScheme
        {
            Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "ApiKey" }
        }] = []
    });
});
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton<IRulePackService, RulePackService>();
builder.Services.AddSingleton<IStaticCheckEngine, StaticCheckEngine>();
builder.Services.AddSingleton<IRuleMatcherService, RuleMatcherService>();
builder.Services.AddSingleton<ICrawlerService, SeleniumCrawlerService>();
builder.Services.AddSingleton<IPdfRulesLoaderService, PdfRulesLoaderService>();
builder.Services.AddSingleton<IManualFindingsImporter, ManualFindingsImporter>();
builder.Services.AddSingleton<IReportGenerator, ReportGenerator>();
builder.Services.AddSingleton<ILlmReviewService, DisabledLlmReviewService>();
builder.Services.AddSingleton<JobStore>();
builder.Services.AddSingleton(ApiKeySecurity.LoadFromEnvironment());

var app = builder.Build();
app.Use(async (context, next) =>
{
    if (!ApiKeySecurity.IsProtectedRoute(context.Request.Path))
    {
        await next();
        return;
    }

    var configuredKeys = context.RequestServices.GetRequiredService<ApiKeyMaterial>();
    if (ApiKeySecurity.IsValid(context.Request.Headers["x-api-key"], configuredKeys))
    {
        await next();
        return;
    }

    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
    await context.Response.WriteAsJsonAsync(new { error = "A valid x-api-key header is required for API routes." });
});

if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("Security:EnableSwagger"))
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("Security:EnableUi"))
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
}

app.MapGet("/", () => Results.Redirect("/ui/"));

app.MapGet("/api/version", () => new
{
    name = ProductInfo.Name,
    version = ProductInfo.Version,
    commit = ProductInfo.Commit,
    buildDateUtc = ProductInfo.BuildDateUtc,
    runtime = ".NET 8",
    disclaimer = ComplianceDisclaimers.Standard
});

app.MapPost("/api/crawl/start", async (CrawlerOptions options, JobStore jobs, IServiceProvider services, IConfiguration configuration, CancellationToken cancellationToken) =>
{
    CrawlerOptions normalizedOptions;
    try
    {
        normalizedOptions = ApiRequestGuards.NormalizeCrawlerOptions(options, configuration);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }

    var job = jobs.Create("crawl");
    _ = Task.Run(async () =>
    {
        using var jobTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(ApiRequestGuards.JobTimeoutMinutes(configuration)));
        var jobToken = jobTimeout.Token;
        try
        {
            using var scope = services.CreateScope();
            var rulePack = scope.ServiceProvider.GetRequiredService<IRulePackService>();
            var crawler = scope.ServiceProvider.GetRequiredService<ICrawlerService>();
            var engine = scope.ServiceProvider.GetRequiredService<IStaticCheckEngine>();
            var matcher = scope.ServiceProvider.GetRequiredService<IRuleMatcherService>();
            var reporting = scope.ServiceProvider.GetRequiredService<IReportGenerator>();
            var pdf = scope.ServiceProvider.GetRequiredService<IPdfRulesLoaderService>();

            var pdfRules = new List<AccessibilityRule>();
            if (normalizedOptions.EnablePdfRuleOverlay && !string.IsNullOrWhiteSpace(normalizedOptions.RulesPdfPath))
            {
                var extracted = await pdf.ExtractAsync(normalizedOptions.RulesPdfPath, Path.Combine(normalizedOptions.OutputDirectory, "rules"), jobToken);
                pdfRules.AddRange(extracted.Rules);
            }

            var rules = await rulePack.LoadRulesAsync(normalizedOptions.EnableSection508Rules, normalizedOptions.EnableWcag22EnhancedRules, pdfRules, jobToken);
            var pages = await crawler.CrawlAsync(normalizedOptions, jobToken);
            var findings = matcher.EnrichFindings(pages.SelectMany(p => engine.Analyze(p, rules)), rules);
            var result = new ScanResult
            {
                Options = normalizedOptions,
                Pages = pages,
                Findings = findings,
                RulesUsed = rules,
                Disclaimer = normalizedOptions.BrowserMode == BrowserMode.EdgeIeModeAssisted ? $"{ComplianceDisclaimers.Standard} {ComplianceDisclaimers.IeMode}" : ComplianceDisclaimers.Standard
            };
            var manifest = await reporting.GenerateAsync(result, normalizedOptions.OutputDirectory, jobToken);
            jobs.Complete(job.Id, new { result.ScanId, manifest });
        }
        catch (Exception ex)
        {
            jobs.Fail(job.Id, ex);
        }
    }, cancellationToken);
    return Results.Accepted($"/api/jobs/{job.Id}", job);
});

app.MapPost("/api/rules/extract", async (RulesExtractRequest request, IPdfRulesLoaderService pdf, IConfiguration configuration, CancellationToken cancellationToken) =>
{
    try
    {
        var output = ApiRequestGuards.ResolveOutputDirectory(request.OutputDirectory, configuration);
        var pdfPath = ApiRequestGuards.ResolvePdfInputPath(request.RulesPdfPath, configuration);
        return Results.Ok(await pdf.ExtractAsync(pdfPath, output, cancellationToken));
    }
    catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
    {
        return Results.BadRequest(new { error = "The requested PDF could not be loaded from the configured input directory." });
    }
});

app.MapPost("/api/findings/import", async (ManualFindingsImportRequest request, IManualFindingsImporter importer, IConfiguration configuration, CancellationToken cancellationToken) =>
{
    try
    {
        var csvPath = ApiRequestGuards.ResolveCsvInputPath(request.CsvPath, configuration);
        return Results.Ok(await importer.ImportAsync(csvPath, cancellationToken));
    }
    catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
    {
        return Results.BadRequest(new { error = "The requested CSV could not be loaded from the configured input directory." });
    }
});

app.MapPost("/api/report/generate", async (ScanResult result, IReportGenerator generator, IConfiguration configuration, CancellationToken cancellationToken) =>
{
    try
    {
        var output = ApiRequestGuards.ResolveOutputDirectory(result.Options.OutputDirectory, configuration);
        return Results.Ok(await generator.GenerateAsync(result with { Options = result.Options with { OutputDirectory = output } }, output, cancellationToken));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/api/jobs/{id}", (string id, JobStore jobs) => jobs.TryGet(id, out var job) ? Results.Ok(job) : Results.NotFound());

app.MapGet("/api/reports/{id}", (string id) =>
{
    var report = Directory.EnumerateFiles("reports", "report.html", SearchOption.AllDirectories)
        .FirstOrDefault(p => p.Contains(id, StringComparison.OrdinalIgnoreCase));
    return report is null ? Results.NotFound() : Results.File(report, "text/html");
});

app.MapGet("/api/reports", () =>
{
    var reportsRoot = Path.GetFullPath("reports");
    if (!Directory.Exists(reportsRoot))
    {
        return Results.Ok(Array.Empty<ReportListItem>());
    }

    var items = Directory.EnumerateFiles(reportsRoot, "report.html", SearchOption.AllDirectories)
        .Select(path => ReportListItem.FromReportPath(reportsRoot, path))
        .OrderByDescending(item => item.LastModifiedUtc)
        .ToList();

    return Results.Ok(items);
});

app.MapGet("/api/report-file", (string path) =>
{
    var reportsRoot = Path.GetFullPath("reports");
    var requested = Path.GetFullPath(Path.Combine(reportsRoot, path));
    var normalizedRoot = Path.TrimEndingDirectorySeparator(reportsRoot) + Path.DirectorySeparatorChar;
    var normalizedRequested = Path.TrimEndingDirectorySeparator(requested) + Path.DirectorySeparatorChar;
    if (!normalizedRequested.StartsWith(normalizedRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) || !File.Exists(requested))
    {
        return Results.NotFound();
    }

    var contentType = Path.GetExtension(requested).ToLowerInvariant() switch
    {
        ".html" => "text/html",
        ".md" => "text/markdown",
        ".json" => "application/json",
        ".csv" => "text/csv",
        ".png" => "image/png",
        _ => "application/octet-stream"
    };

    return Results.File(requested, contentType, Path.GetFileName(requested));
});

app.MapGet("/api/rulepacks", async (IRulePackService rules, CancellationToken cancellationToken) =>
    await rules.LoadRulesAsync(includeSection508: true, includeWcag22: true, cancellationToken: cancellationToken));

app.MapGet("/api/rulepacks/{id}", async (string id, IRulePackService rules, CancellationToken cancellationToken) =>
{
    var all = await rules.LoadRulesAsync(includeSection508: true, includeWcag22: true, cancellationToken: cancellationToken);
    return Results.Ok(all.Where(r => r.Standard.Contains(id, StringComparison.OrdinalIgnoreCase) || r.RuleId.Contains(id, StringComparison.OrdinalIgnoreCase)));
});

app.Run();

public sealed record RulesExtractRequest(string RulesPdfPath, string OutputDirectory);
public sealed record ManualFindingsImportRequest(string CsvPath);
public sealed record JobStatus(string Id, string Type, string Status, DateTimeOffset CreatedAtUtc, object? Result = null, string? Error = null);
public sealed record ReportListItem(
    string Name,
    string Directory,
    DateTimeOffset LastModifiedUtc,
    string HtmlPath,
    string MarkdownPath,
    string JsonPath,
    string FindingsCsvPath,
    string AdoCsvPath,
    string ExecutiveSummaryPath)
{
    public static ReportListItem FromReportPath(string reportsRoot, string htmlPath)
    {
        var directory = Path.GetDirectoryName(htmlPath) ?? reportsRoot;
        var relativeDirectory = Path.GetRelativePath(reportsRoot, directory);
        static string Relative(string reportsRoot, string directory, string fileName) =>
            Path.GetRelativePath(reportsRoot, Path.Combine(directory, fileName));

        return new ReportListItem(
            string.IsNullOrWhiteSpace(relativeDirectory) || relativeDirectory == "." ? "reports" : relativeDirectory,
            relativeDirectory,
            File.GetLastWriteTimeUtc(htmlPath),
            Relative(reportsRoot, directory, "report.html"),
            Relative(reportsRoot, directory, "report.md"),
            Relative(reportsRoot, directory, "report.json"),
            Relative(reportsRoot, directory, "findings.csv"),
            Relative(reportsRoot, directory, "ado-items.csv"),
            Relative(reportsRoot, directory, "executive-summary.md"));
    }
}

public sealed class JobStore
{
    private readonly ConcurrentDictionary<string, JobStatus> _jobs = new();
    public JobStatus Create(string type)
    {
        var job = new JobStatus(Guid.NewGuid().ToString("N"), type, "Running", DateTimeOffset.UtcNow);
        _jobs[job.Id] = job;
        return job;
    }
    public void Complete(string id, object result) => _jobs[id] = _jobs[id] with { Status = "Completed", Result = result };
    public void Fail(string id, Exception exception) => _jobs[id] = _jobs[id] with { Status = "Failed", Error = exception.Message };
    public bool TryGet(string id, out JobStatus? job) => _jobs.TryGetValue(id, out job);
}

public static class ApiKeySecurity
{
    public static bool IsProtectedRoute(PathString path) =>
        path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWithSegments("/swagger", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWithSegments("/ui", StringComparison.OrdinalIgnoreCase);

    public static ApiKeyMaterial LoadFromEnvironment()
    {
        var keys = new[]
            {
                Environment.GetEnvironmentVariable("ApiSecurity__ApiKeys__0"),
                Environment.GetEnvironmentVariable("LEGACY_A11Y_API_KEY")
            }
            .Concat((Environment.GetEnvironmentVariable("LEGACY_A11Y_API_KEYS") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(key => !string.IsNullOrWhiteSpace(key) && key.Length >= 16)
            .Distinct(StringComparer.Ordinal)
            .Select(key => Hash(key!))
            .ToArray();

        if (keys.Length == 0)
        {
            throw new InvalidOperationException("No API key is configured. Set ApiSecurity__ApiKeys__0 or LEGACY_A11Y_API_KEY in the environment.");
        }

        return new ApiKeyMaterial(keys);
    }

    public static bool IsValid(string? suppliedKey, ApiKeyMaterial configuredKeys)
    {
        if (string.IsNullOrWhiteSpace(suppliedKey))
        {
            return false;
        }

        var suppliedHash = Hash(suppliedKey);
        return configuredKeys.Sha256Hashes.Any(hash => CryptographicOperations.FixedTimeEquals(hash, suppliedHash));
    }

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim()));
}

public sealed record ApiKeyMaterial(IReadOnlyList<byte[]> Sha256Hashes);

public static class ApiRequestGuards
{
    public static CrawlerOptions NormalizeCrawlerOptions(CrawlerOptions options, IConfiguration configuration)
    {
        var allowedDomains = configuration.GetSection("Crawler:AllowedDomains").Get<string[]>() ?? [];
        var security = SecurityOptions(configuration);
        var normalized = CrawlRequestValidator.ValidateAndNormalize(
            options,
            allowedDomains,
            BaseOutputDirectory(configuration),
            security.AllowPrivateNetworkTargets,
            security.MaxPagesLimit,
            security.MaxDepthLimit,
            security.MaxDelaySecondsLimit);

        if (!string.IsNullOrWhiteSpace(normalized.RulesPdfPath))
        {
            normalized = normalized with { RulesPdfPath = ResolvePdfInputPath(normalized.RulesPdfPath, configuration) };
        }

        return normalized;
    }

    public static string ResolveOutputDirectory(string? requestedPath, IConfiguration configuration) =>
        OutputPathSanitizer.ResolveOutputDirectory(requestedPath, BaseOutputDirectory(configuration));

    public static string ResolvePdfInputPath(string requestedPath, IConfiguration configuration)
    {
        var input = FileInputOptions(configuration);
        return FileInputValidator.ResolveExistingFile(requestedPath, input.BaseInputDirectory, ".pdf", input.MaxPdfBytes);
    }

    public static string ResolveCsvInputPath(string requestedPath, IConfiguration configuration)
    {
        var input = FileInputOptions(configuration);
        return FileInputValidator.ResolveExistingFile(requestedPath, input.BaseInputDirectory, ".csv", input.MaxCsvBytes);
    }

    public static int JobTimeoutMinutes(IConfiguration configuration) =>
        Math.Clamp(configuration.GetValue<int?>("Security:JobTimeoutMinutes") ?? 30, 1, 240);

    private static string BaseOutputDirectory(IConfiguration configuration) =>
        configuration.GetValue<string>("Security:BaseOutputDirectory") ?? "reports";

    private static string BaseInputDirectory(IConfiguration configuration) =>
        configuration.GetValue<string>("Security:BaseInputDirectory") ?? "inputs";

    private static CrawlSecurityOptions SecurityOptions(IConfiguration configuration) => new()
    {
        AllowPrivateNetworkTargets = configuration.GetValue<bool>("Security:AllowPrivateNetworkTargets"),
        MaxPagesLimit = configuration.GetValue<int?>("Security:MaxPagesLimit") ?? 100,
        MaxDepthLimit = configuration.GetValue<int?>("Security:MaxDepthLimit") ?? 5,
        MaxDelaySecondsLimit = configuration.GetValue<int?>("Security:MaxDelaySecondsLimit") ?? 30,
        JobTimeoutMinutes = JobTimeoutMinutes(configuration)
    };

    private static FileInputOptions FileInputOptions(IConfiguration configuration) => new()
    {
        BaseInputDirectory = BaseInputDirectory(configuration),
        MaxPdfBytes = configuration.GetValue<long?>("Security:MaxPdfBytes") ?? 25 * 1024 * 1024,
        MaxCsvBytes = configuration.GetValue<long?>("Security:MaxCsvBytes") ?? 5 * 1024 * 1024
    };
}
