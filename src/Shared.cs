using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using NLog;

namespace CanvasCourseExporter;

/// <summary>
/// Shared Canvas API client with pagination, rate limit handling, and retry logic.
/// Single HttpClient instance — all Canvas traffic flows through here so rate limit
/// tracking sees the full request volume.
/// </summary>
static class CanvasApi
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();
    private static HttpClient _http = null!;
    private static string _baseUrl = null!;

    public static void Initialize(string baseUrl, string apiToken)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public static string BaseUrl => _baseUrl;

    /// <summary>
    /// GET a single JSON object from the API.
    /// </summary>
    public static async Task<JsonDocument?> GetAsync(string relativeUrl)
    {
        var url = relativeUrl.StartsWith("http") ? relativeUrl : $"{_baseUrl}{relativeUrl}";
        var response = await SendWithRetryAsync(url);

        if (!response.IsSuccessStatusCode)
            return null;

        var json = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(json);
    }

    /// <summary>
    /// POST form data to the API and return the JSON response.
    /// Used for triggering content exports and other Canvas actions.
    /// </summary>
    public static async Task<JsonDocument?> PostFormAsync(
        string relativeUrl, IEnumerable<KeyValuePair<string, string>> formData)
    {
        var url = relativeUrl.StartsWith("http") ? relativeUrl : $"{_baseUrl}{relativeUrl}";
        var content = new FormUrlEncodedContent(formData);
        var response = await _http.PostAsync(url, content);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            Log.Error("POST {Url} failed ({StatusCode}): {Error}",
                relativeUrl, (int)response.StatusCode, error);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(json);
    }

    /// <summary>
    /// GET a paginated list of JSON objects, following Link headers automatically.
    /// </summary>
    public static async Task<List<JsonElement>> GetPaginatedAsync(string relativeUrl, int perPage = 100)
    {
        var results = new List<JsonElement>();
        var url = relativeUrl.StartsWith("http") ? relativeUrl : $"{_baseUrl}{relativeUrl}";

        url += url.Contains('?') ? $"&per_page={perPage}" : $"?per_page={perPage}";

        while (!string.IsNullOrEmpty(url))
        {
            var response = await SendWithRetryAsync(url);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            foreach (var item in doc.RootElement.EnumerateArray())
                results.Add(item.Clone());

            url = GetNextPageUrl(response);
        }

        return results;
    }

    /// <summary>
    /// Download a file from a URL, streaming directly to disk.
    /// Uses a separate HttpClient without auth headers (S3 presigned URLs reject them).
    /// Streams via ResponseHeadersRead to avoid buffering large files in memory.
    /// </summary>
    public static async Task DownloadFileAsync(string downloadUrl, string destPath)
    {
        using var client = new HttpClient();
        using var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using var contentStream = await response.Content.ReadAsStreamAsync();
        await using var fs = File.Create(destPath);
        await contentStream.CopyToAsync(fs);
    }

    /// <summary>
    /// Small delay to stay under rate limits between operations.
    /// </summary>
    public static Task DelayAsync(int ms = 200) => Task.Delay(ms);

    // --------------------------------------------------------------------
    private static async Task<HttpResponseMessage> SendWithRetryAsync(string url, int maxRetries = 3)
    {
        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            var response = await _http.GetAsync(url);

            if (response.Headers.TryGetValues("X-Rate-Limit-Remaining", out var remaining))
            {
                if (double.TryParse(remaining.First(), out var left) && left < 50)
                {
                    Log.Debug("Rate limit low: {Remaining} remaining, pausing...", left);
                    await Task.Delay(2000);
                }
            }

            if ((int)response.StatusCode == 429 || (int)response.StatusCode == 403)
            {
                var wait = 10;
                if (response.Headers.TryGetValues("Retry-After", out var ra))
                    int.TryParse(ra.First(), out wait);

                if (attempt < maxRetries)
                {
                    Log.Warn("Rate limited ({StatusCode}) — waiting {Wait}s (attempt {Attempt}/{Max})",
                        (int)response.StatusCode, wait, attempt + 1, maxRetries);
                    await Task.Delay(wait * 1000);
                    continue;
                }
            }

            return response;
        }

        throw new HttpRequestException("Max retries exceeded.");
    }

    private static string? GetNextPageUrl(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Link", out var headers))
            return null;

        var link = string.Join(", ", headers);
        var match = Regex.Match(link, @"<([^>]+)>;\s*rel=""next""");
        return match.Success ? match.Groups[1].Value : null;
    }
}

/// <summary>
/// Utilities for handling HTML content from Canvas.
/// </summary>
static class HtmlUtils
{
    /// <summary>
    /// Strip HTML tags to produce readable plain text.
    /// </summary>
    public static string StripHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";

        var text = Regex.Replace(html, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</?(p|div|h[1-6]|li|tr|blockquote)[^>]*>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</(td|th)>", "\t", RegexOptions.IgnoreCase);

        text = Regex.Replace(text, @"<a[^>]+href=""([^""]+)""[^>]*>(.*?)</a>",
            "[$2]($1)", RegexOptions.IgnoreCase | RegexOptions.Singleline);

        text = Regex.Replace(text, @"<[^>]+>", "");
        text = System.Net.WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"\n{3,}", "\n\n");

        return text.Trim();
    }

    /// <summary>
    /// Extract all Canvas file IDs referenced in an HTML body.
    /// </summary>
    public static List<long> ExtractFileIds(string html)
    {
        var ids = new List<long>();
        var patterns = new[]
        {
            @"/courses/\d+/files/(\d+)",
            @"/files/(\d+)(?:/download|/preview|\?|"")",
            @"data-api-endpoint=""[^""]*files/(\d+)",
        };

        foreach (var pattern in patterns)
            foreach (Match m in Regex.Matches(html, pattern))
                if (long.TryParse(m.Groups[1].Value, out var id))
                    ids.Add(id);

        return ids.Distinct().ToList();
    }

    /// <summary>
    /// Extract external (non-Canvas) URLs from HTML.
    /// </summary>
    public static List<string> ExtractExternalLinks(string html)
    {
        return Regex.Matches(html, @"href=""(https?://[^""]+)""")
            .Select(m => m.Groups[1].Value)
            .Where(u => !u.Contains("instructure.com") && !u.Contains("/courses/") && !u.Contains("/files/"))
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// Wrap raw Canvas HTML in a styled document for standalone viewing.
    /// </summary>
    public static string WrapHtml(string body, string title, string? subtitle = null)
    {
        return $@"<!DOCTYPE html>
<html>
<head>
    <meta charset=""utf-8"">
    <title>{System.Net.WebUtility.HtmlEncode(title)}</title>
    <style>
        body {{ font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif;
               max-width: 900px; margin: 40px auto; padding: 0 20px;
               line-height: 1.6; color: #333; }}
        .header {{ border-bottom: 2px solid #2d3b45; padding-bottom: 12px; margin-bottom: 24px; }}
        .header h1 {{ margin: 0 0 4px 0; color: #2d3b45; font-size: 1.4em; }}
        .header .meta {{ color: #666; font-size: 0.9em; }}
        table {{ border-collapse: collapse; width: 100%; }}
        td, th {{ border: 1px solid #ddd; padding: 8px; text-align: left; }}
        th {{ background: #f5f5f5; }}
        img {{ max-width: 100%; height: auto; }}
        a {{ color: #0077c8; }}
    </style>
</head>
<body>
    <div class=""header"">
        <h1>{System.Net.WebUtility.HtmlEncode(title)}</h1>
        {(subtitle != null ? $"<div class=\"meta\">{System.Net.WebUtility.HtmlEncode(subtitle)}</div>" : "")}
        <div class=""meta"">Exported {DateTime.Now:MMMM dd, yyyy}</div>
    </div>
    {body}
</body>
</html>";
    }
}

/// <summary>
/// File system helpers.
/// </summary>
static class FileUtils
{
    public static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "unnamed" : sanitized;
    }

    /// <summary>
    /// Sanitize a ZIP entry's full path for Windows filesystem.
    /// Preserves directory separators but replaces illegal characters
    /// within each path segment.
    /// </summary>
    public static string SanitizeEntryPath(string entryPath)
    {
        var parts = entryPath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        var invalidChars = Path.GetInvalidFileNameChars();

        for (int i = 0; i < parts.Length; i++)
        {
            parts[i] = new string(parts[i].Select(c => invalidChars.Contains(c) ? '_' : c).ToArray()).Trim();
            parts[i] = parts[i].TrimEnd('.', ' ');

            if (string.IsNullOrWhiteSpace(parts[i]))
                parts[i] = "_";
        }

        return Path.Combine(parts);
    }

    public static string GetUniqueFilePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path) ?? ".";
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        int n = 2;
        while (File.Exists(path))
        {
            path = Path.Combine(dir, $"{name} ({n}){ext}");
            n++;
        }
        return path;
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):F1} MB",
        _ => $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB"
    };

    public static string FormatSize(long? bytes) =>
        bytes.HasValue ? FormatSize(bytes.Value) : "unknown size";

    /// <summary>
    /// Download all Canvas file IDs found embedded in an HTML body into a target folder.
    /// Returns count of files downloaded.
    /// </summary>
    public static async Task<int> DownloadEmbeddedFilesAsync(string html, string destDir)
    {
        var fileIds = HtmlUtils.ExtractFileIds(html);
        if (fileIds.Count == 0) return 0;

        Directory.CreateDirectory(destDir);
        int count = 0;

        foreach (var fileId in fileIds)
        {
            try
            {
                using var doc = await CanvasApi.GetAsync($"/api/v1/files/{fileId}");
                if (doc == null) continue;

                var root = doc.RootElement;
                var fileName = root.TryGetProperty("display_name", out var dn)
                    ? dn.GetString() ?? $"file_{fileId}" : $"file_{fileId}";

                string? downloadUrl = null;
                if (root.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
                    downloadUrl = u.GetString();

                if (string.IsNullOrWhiteSpace(downloadUrl)) continue;

                var destPath = GetUniqueFilePath(Path.Combine(destDir, SanitizeFileName(fileName)));
                await CanvasApi.DownloadFileAsync(downloadUrl, destPath);
                count++;

                await CanvasApi.DelayAsync();
            }
            catch
            {
                // Best effort — don't fail the whole export for one embedded file
            }
        }

        return count;
    }
}