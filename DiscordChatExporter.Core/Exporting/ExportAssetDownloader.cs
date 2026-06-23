using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using AsyncKeyedLock;
using DiscordChatExporter.Core.Utils;
using DiscordChatExporter.Core.Utils.Extensions;

namespace DiscordChatExporter.Core.Exporting;

internal partial class ExportAssetDownloader(string workingDirPath, bool reuse = false)
{
    private static readonly AsyncKeyedLocker<string> Locker = new();

    // File paths of the previously downloaded assets
    private readonly Dictionary<string, string> _previousPathsByUrl = new(StringComparer.Ordinal);

    // Kept for API compatibility
    private readonly bool _reuse = reuse;

    public async ValueTask<string> DownloadAsync(
        string url,
        string? authorSubDir = null,
        CancellationToken cancellationToken = default
    )
    {
        var targetDir = authorSubDir is not null
            ? Path.Combine(workingDirPath, authorSubDir)
            : workingDirPath;

        var fileName = GetFileNameFromUrl(url);
        var filePath = Path.Combine(targetDir, fileName);

        if (_previousPathsByUrl.TryGetValue(url, out var cachedFilePath))
            return cachedFilePath;

        Directory.CreateDirectory(targetDir);

        var actualFilePath = filePath;
        await Http.ResiliencePipeline.ExecuteAsync(
            async innerCancellationToken =>
            {
                using var response = await Http.Client.GetAsync(url, innerCancellationToken);

                // Prefer the file name from Content-Disposition if available
                var cdFileName =
                    response.Content.Headers.ContentDisposition?.FileNameStar
                    ?? GetFileNameFromContentDisposition(
                        response.Content.Headers.ContentDisposition?.ToString()
                    );

                string baseName;
                string ext;
                if (!string.IsNullOrWhiteSpace(cdFileName))
                {
                    baseName = Path.GetFileNameWithoutExtension(cdFileName).Truncate(60);
                    ext = Path.GetExtension(cdFileName);
                }
                else
                {
                    var urlFileName = Regex.Match(url, @".+/([^?]*)").Groups[1].Value;
                    baseName = Path.GetFileNameWithoutExtension(urlFileName).Truncate(60);
                    ext = Path.GetExtension(urlFileName);
                }

                // First file keeps original name; duplicates get -v2, -v3, etc.
                var candidate = Path.Combine(targetDir, Path.EscapeFileName(baseName + ext));
                var version = 2;
                while (File.Exists(candidate))
                {
                    candidate = Path.Combine(
                        targetDir,
                        Path.EscapeFileName(baseName + $"-v{version}" + ext)
                    );
                    version++;
                }
                actualFilePath = candidate;

                await using var output = File.Create(actualFilePath);
                await response.Content.CopyToAsync(output, innerCancellationToken);
            },
            cancellationToken
        );

        return _previousPathsByUrl[url] = actualFilePath;
    }
}

internal partial class ExportAssetDownloader
{
    private static string? GetFileNameFromContentDisposition(string? contentDisposition)
    {
        if (string.IsNullOrEmpty(contentDisposition))
            return null;

        // Try filename* (RFC 5987) first: filename*=UTF-8''encoded-name
        var starMatch = Regex.Match(
            contentDisposition,
            @"filename\*\s*=\s*UTF-8''([^;\s]+)",
            RegexOptions.IgnoreCase
        );
        if (starMatch.Success)
            return Uri.UnescapeDataString(starMatch.Groups[1].Value);

        // Fall back to filename="name"
        var match = Regex.Match(
            contentDisposition,
            @"filename\s*=\s*""?([^"";\s]+)""?",
            RegexOptions.IgnoreCase
        );
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string NormalizeUrl(string url)
    {
        // Remove signature parameters from Discord CDN URLs to normalize them
        var uri = new Uri(url);
        if (!string.Equals(uri.Host, "cdn.discordapp.com", StringComparison.OrdinalIgnoreCase))
            return url;

        var query = HttpUtility.ParseQueryString(uri.Query);
        query.Remove("ex");
        query.Remove("is");
        query.Remove("hm");

        return uri.GetLeftPart(UriPartial.Path) + query;
    }

    private static string GetFileNameFromUrl(string url, string urlHash)
    {
        // Try to extract the file name from URL
        var fileName = Regex.Match(url, @".+/([^?]*)").Groups[1].Value;

        // If it's not there, just use the URL hash as the file name
        if (string.IsNullOrWhiteSpace(fileName))
            return urlHash;

        // Otherwise, use the original file name but inject the hash in the middle
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        var fileExtension = Path.GetExtension(fileName);

        // Probably not a file extension, just a dot in a long file name
        // https://github.com/Tyrrrz/DiscordChatExporter/pull/812
        if (fileExtension.Length > 41)
        {
            fileNameWithoutExtension = fileName;
            fileExtension = "";
        }

        return Path.EscapeFileName(
            fileNameWithoutExtension.Truncate(42) + '-' + urlHash + fileExtension
        );
    }

    private static string GetFileNameFromUrl(string url) =>
        GetFileNameFromUrl(
            url,
            // 16 chars = 64 bits, reaches 1% collision probability at ~609 million files
            SHA256
                .HashData(Encoding.UTF8.GetBytes(NormalizeUrl(url)))
                .Pipe(Convert.ToHexStringLower)
                .Truncate(16)
        );

    // Legacy naming used a 5-char hash, kept for backwards compatibility with existing exports
    private static string GetLegacyFileNameFromUrl(string url) =>
        GetFileNameFromUrl(
            url,
            SHA256
                .HashData(Encoding.UTF8.GetBytes(NormalizeUrl(url)))
                .Pipe(Convert.ToHexStringLower)
                // 5 chars = 20 bits, reaches 1% collision probability at ~145 files
                .Truncate(5)
        );
}
