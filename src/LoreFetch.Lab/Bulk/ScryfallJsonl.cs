using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace LoreFetch.Lab.Bulk;

/// Streams one parsed `JsonElement` per line of a Scryfall bulk-data file.
///
/// Scryfall bulk data is gzipped JSONL -- one JSON object per line, no
/// enclosing array -- so this never materializes the whole file: it reads
/// one line at a time from a `GZipStream` and parses only that line.
/// `default_cards` is hundreds of MB uncompressed, so loading it whole
/// would defeat the point of streaming.
///
/// Transparently reads a plain (non-gzipped) file too, detected by the
/// gzip magic bytes rather than the file extension, so the exact same
/// method serves both a real `*.jsonl.gz` download and a small committed
/// `*.jsonl` test fixture.
public static class ScryfallJsonl
{
    public static IEnumerable<JsonElement> ReadLines(string path)
    {
        using var stream = OpenDecompressed(path);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0)
            {
                continue;
            }

            using var doc = JsonDocument.Parse(line);

            // Clone: the JsonDocument (and the buffer it owns) is disposed
            // at the end of this loop iteration, before the caller sees
            // the next element via yield return, so the returned element
            // must own its own memory.
            yield return doc.RootElement.Clone();
        }
    }

    private static Stream OpenDecompressed(string path)
    {
        var file = File.OpenRead(path);

        Span<byte> magic = stackalloc byte[2];
        var read = file.Read(magic);
        file.Position = 0;

        var isGzip = read == 2 && magic[0] == 0x1f && magic[1] == 0x8b;
        return isGzip ? new GZipStream(file, CompressionMode.Decompress) : file;
    }
}
