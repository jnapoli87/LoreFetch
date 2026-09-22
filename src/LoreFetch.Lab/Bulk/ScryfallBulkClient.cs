using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace LoreFetch.Lab.Bulk;

/// Resolves and downloads a Scryfall bulk-data file (`unique_artwork` for
/// `bulk`, `default_cards` for `printings`).
///
/// Both mandatory headers are set explicitly on the client, never left to
/// the library default, per Scryfall's own docs: "Do not allow HTTP
/// libraries to choose the header for you." The download filename is
/// never hardcoded -- it carries a daily timestamp -- and is read back
/// from `jsonl_download_uri` every time.
public sealed class ScryfallBulkClient
{
    // Shared with LoreFetch.Lab.Images.ImageDownloader (B4b) -- one mandatory
    // User-Agent string for every Scryfall request this tool makes, never a
    // second copy that could drift from this one.
    internal const string UserAgent = "LoreFetch/0.1 (github.com/jnapoli87/LoreFetch)";
    private static readonly TimeSpan MinimumRetryAfter = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;

    public ScryfallBulkClient(HttpClient http)
    {
        _http = http;
    }

    public static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    /// Downloads `GET /bulk-data/{bulkType}`'s `jsonl_download_uri` into
    /// `outDir`, skipping the download entirely when a file of that exact
    /// name and size already exists there. Returns the local path.
    public async Task<string> DownloadBulkFileAsync(string bulkType, string outDir, CancellationToken ct)
    {
        var metadata = await GetBulkMetadataAsync(bulkType, ct);
        var fileName = Path.GetFileName(new Uri(metadata.DownloadUri).LocalPath);
        var destPath = Path.Combine(outDir, fileName);

        if (File.Exists(destPath) && new FileInfo(destPath).Length == metadata.CompressedSize)
        {
            Console.WriteLine($"Already have {fileName} ({metadata.CompressedSize} bytes) -- skipping download.");
            return destPath;
        }

        Directory.CreateDirectory(outDir);
        Console.WriteLine($"Downloading {fileName} ({metadata.CompressedSize} bytes)...");
        await DownloadToFileAsync(metadata.DownloadUri, destPath, ct);
        return destPath;
    }

    private async Task<BulkMetadata> GetBulkMetadataAsync(string bulkType, CancellationToken ct)
    {
        var uri = $"https://api.scryfall.com/bulk-data/{bulkType}";
        using var response = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, uri), ct);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;

        // jsonl_download_uri / compressed_size, NOT download_uri / size --
        // those fields are gone from the live API.
        var downloadUri = root.GetProperty("jsonl_download_uri").GetString()
            ?? throw new FormatException($"bulk-data/{bulkType} response had no jsonl_download_uri.");
        var compressedSize = root.GetProperty("compressed_size").GetInt64();

        return new BulkMetadata(downloadUri, compressedSize);
    }

    private async Task DownloadToFileAsync(string uri, string destPath, CancellationToken ct)
    {
        using var response = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, uri), ct);
        response.EnsureSuccessStatusCode();

        // Download to a temp file in the same directory then move into
        // place, so a failed/cancelled download never leaves a partial
        // file at the path the size check above treats as "already have."
        var tempPath = destPath + ".download";
        await using (var fileStream = File.Create(tempPath))
        await using (var contentStream = await response.Content.ReadAsStreamAsync(ct))
        {
            await contentStream.CopyToAsync(fileStream, ct);
        }

        File.Move(tempPath, destPath, overwrite: true);
    }

    /// Scryfall: "It is not acceptable to ignore HTTP 429 responses."
    /// Waits for `Retry-After` when present, otherwise the documented 30 s
    /// lockout -- and never less than 30 s even if a server sent a smaller
    /// value, since that lockout is the one Scryfall promises.
    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken ct)
    {
        while (true)
        {
            var response = await _http.SendAsync(
                requestFactory(),
                HttpCompletionOption.ResponseHeadersRead,
                ct);

            if (response.StatusCode != HttpStatusCode.TooManyRequests)
            {
                return response;
            }

            var wait = response.Headers.RetryAfter?.Delta ?? MinimumRetryAfter;
            if (wait < MinimumRetryAfter)
            {
                wait = MinimumRetryAfter;
            }

            response.Dispose();
            Console.WriteLine($"429 Too Many Requests -- waiting {wait.TotalSeconds:F0}s before retry.");
            await Task.Delay(wait, ct);
        }
    }

    private sealed record BulkMetadata(string DownloadUri, long CompressedSize);
}
