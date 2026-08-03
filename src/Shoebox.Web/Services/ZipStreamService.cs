using System.IO.Compression;
using Shoebox.Web.Data;

namespace Shoebox.Web.Services;

public class ZipStreamService(StoragePaths paths)
{
    /// <summary>
    /// Streams the given files as a ZIP directly into <paramref name="output"/>,
    /// no temp files, memory stays flat regardless of pool size. Files are stored,
    /// not recompressed (they're already compressed formats).
    /// </summary>
    public async Task WriteAsync(IEnumerable<Media> items, Stream output, CancellationToken ct = default)
    {
        await using var zip = await ZipArchive.CreateAsync(output, ZipArchiveMode.Create, leaveOpen: true,
            entryNameEncoding: null, cancellationToken: ct);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            var sourcePath = paths.OriginalFile(item.PoolId, item.Id, item.Extension);
            if (!File.Exists(sourcePath))
            {
                continue;
            }

            var entry = zip.CreateEntry(UniqueEntryName(item, usedNames), CompressionLevel.NoCompression);
            entry.LastWriteTime = item.SortDate;

            await using var entryStream = await entry.OpenAsync(ct);
            await using var source = File.OpenRead(sourcePath);
            await source.CopyToAsync(entryStream, ct);
        }
    }

    private static string UniqueEntryName(Media item, HashSet<string> used)
    {
        var uploader = Sanitize(item.UploaderName);
        var baseName = Sanitize(Path.GetFileNameWithoutExtension(item.OriginalFileName));
        if (baseName.Length == 0)
        {
            baseName = item.Id.ToString("N")[..8];
        }

        var stem = uploader.Length > 0 ? $"{uploader}_{baseName}" : baseName;
        var name = stem + item.Extension;
        for (var i = 2; !used.Add(name); i++)
        {
            name = $"{stem}_{i}{item.Extension}";
        }

        return name;
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Trim().Select(c => invalid.Contains(c) || c is '/' or '\\' ? '_' : c).ToArray();
        return new string(chars);
    }
}
