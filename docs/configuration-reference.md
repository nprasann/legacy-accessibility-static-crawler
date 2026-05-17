# Configuration Reference

This page explains the settings used by the API/UI and CLI workflows. For non-technical users, the safest path is to start with the API/UI package and change only the website URL, page limit, browser mode, and output folder.

## Security Settings

| Key | Type | Default | What It Does |
| --- | --- | --- | --- |
| `ApiSecurity__ApiKeys__0` or `LEGACY_A11Y_API_KEY` | environment variable | none | API key accepted in the `x-api-key` header. The API fails closed when no key is configured. |
| `Security:BaseOutputDirectory` | string | `reports` | The only folder where API-generated reports may be written. Output paths outside this folder are rejected. |
| `Security:BaseInputDirectory` | string | `inputs` | The only folder where API PDF/CSV imports may read files. |
| `Security:AllowPrivateNetworkTargets` | boolean | `false` | Allows loopback, private, and link-local crawl targets only when explicitly enabled. |
| `Security:MaxPagesLimit` | number | `100` | Maximum `MaxPages` accepted by the API. |
| `Security:MaxDepthLimit` | number | `5` | Maximum crawl depth accepted by the API. |
| `Security:MaxDelaySecondsLimit` | number | `30` | Maximum page delay accepted by the API. |
| `Security:JobTimeoutMinutes` | number | `30` | API crawl job timeout. |
| `Security:EnableSwagger` | boolean | `false` | Enables Swagger outside development. Protected by API key when enabled. |
| `Security:EnableUi` | boolean | `false` | Enables the browser UI outside development. Protected by API key when enabled. |
| `Crawler:AllowedDomains` | string array | empty | Domains the API is allowed to crawl. Add the authorized target host before crawling from the UI or Swagger. Wildcards like `*.example.gov` are supported. |

Environment variable examples:

```bash
ApiSecurity__ApiKeys__0="replace-with-a-local-secret"
Crawler__AllowedDomains__0="example.gov"
Security__BaseOutputDirectory="reports"
```

## Crawler Settings

| Key | Type | Default | What It Does |
| --- | --- | --- | --- |
| `Crawler:BrowserMode` | string | `modern-edge` | Browser mode: `modern-edge`, `chrome`, or `edge-ie-mode-assisted`. |
| `Crawler:MaxPages` | number | `25` | Maximum pages to crawl. Start small for authenticated or legacy systems. |
| `Crawler:CrawlDepth` | number | `2` | Link depth from the starting page. |
| `Crawler:DelaySeconds` | number | `1` | Delay after navigation before capture. Helps slower legacy pages render. |
| `Crawler:SameDomainOnly` | boolean | `true` | Keeps crawling on the starting domain. |
| `Crawler:RespectRobotsTxt` | boolean | `true` | Intended crawler behavior for sites that publish robots rules. |
| `Crawler:CaptureScreenshots` | boolean | `true` | Saves screenshots as evidence. |
| `Crawler:CaptureHtml` | boolean | `true` | Saves raw HTML captures. These files may contain sensitive data. |
| `Crawler:EnableKeyboardChecks` | boolean | `true` | Enables keyboard/focus evidence collection where supported. |
| `Crawler:EnableWcag22EnhancedRules` | boolean | `false` | Adds optional WCAG 2.2 static rules. |
| `Crawler:EnableSection508Rules` | boolean | `true` | Includes Section 508 static rule mappings. |
| `Crawler:EnablePdfRuleOverlay` | boolean | `true` | Allows PDF-derived agency guidance to enrich findings. |
| `Crawler:RedactQueryStrings` | boolean | `true` | Removes query strings from stored page URLs by default. |

## API Key Use In The UI

The UI has an API key box at the top of the crawl form. Use the value configured in `ApiSecurity__ApiKeys__0` or `LEGACY_A11Y_API_KEY`. No default key is accepted.

## Safe Output Paths

The API rejects output folders that:

- contain `..`
- point to a UNC/network path
- are absolute paths outside `Security:BaseOutputDirectory`

This protects shared workstations from accidental writes outside the approved report folder.
