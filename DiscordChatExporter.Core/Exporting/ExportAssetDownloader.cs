using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using AsyncKeyedLock;
using DiscordChatExporter.Core.Utils;
using PowerKit.Extensions;

namespace DiscordChatExporter.Core.Exporting;

internal partial class ExportAssetDownloader(string workingDirPath, bool reuse = false)
{
    private static readonly AsyncKeyedLocker<string> Locker = new();

    // File paths of the previously downloaded assets
    private readonly Dictionary<string, string> _previousPathsByUrl = new(StringComparer.Ordinal);

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

        // Reuse existing files if we're allowed to
        if (reuse && File.Exists(filePath))
            return _previousPathsByUrl[url] = filePath;

        // Check for a file cached by the legacy naming scheme (5-char hash) and rename it
        // to the new naming scheme to preserve backwards compatibility with existing exports.
        // This will catch both the 5-char lowercase hash and the 5-char uppercase hash variants.
        if (reuse)
        {
            var legacyFileNames = GetLegacyFileNamesFromUrl(url);
            foreach (var legacyFileName in legacyFileNames)
            {
                var legacyFilePath = Path.Combine(workingDirPath, legacyFileName);
                if (File.Exists(legacyFilePath))
                {
                    // Overwrite in case the destination file was created concurrently between our
                    // earlier existence check and this move operation
                    try
                    {
                        File.Move(legacyFilePath, filePath, true);
                        return _previousPathsByUrl[url] = filePath;
                    }
                    catch (IOException)
                    {
                        // The legacy file was moved or deleted concurrently or something else happened.
                        // Upgrading old files is not crucial, so we can just move on.
                    }
                }
            }
        }

        Directory.CreateDirectory(targetDir);

        var actualFilePath = filePath;
        await Http.ResiliencePipeline.ExecuteAsync(
            async innerCancellationToken =>
            {
                // Download the file
                using var response = await Http.Client.GetAsync(
                    url,
                    HttpCompletionOption.ResponseHeadersRead,
                    innerCancellationToken
                );

                response.EnsureSuccessStatusCode();

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
                    var urlFileName = new Uri(url, UriKind.RelativeOrAbsolute).TryGetFileName();
                    baseName = Path.GetFileNameWithoutExtension(urlFileName ?? fileName)
                        .Truncate(60);
                    ext = Path.GetExtension(urlFileName ?? fileName);
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
        // Remove signature parameters from Discord CDN/media URLs to normalize them
        var uri = new Uri(url);

        if (
            !string.Equals(uri.Host, "cdn.discordapp.com", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Host, "media.discordapp.net", StringComparison.OrdinalIgnoreCase)
        )
        {
            return url;
        }

        var query = HttpUtility.ParseQueryString(uri.Query);
        query.Remove("ex");
        query.Remove("is");
        query.Remove("hm");

        return uri.GetLeftPart(UriPartial.Path) + query;
    }

    private static string GetFileNameFromUrl(string url, string urlHash)
    {
        // Try to extract the file name from URL
        var fileName = new Uri(url, UriKind.RelativeOrAbsolute).TryGetFileName();

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
    private static IReadOnlyList<string> GetLegacyFileNamesFromUrl(string url)
    {
        var hashData = SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeUrl(url)));

        return
        [
            // Lowercase variant (introduced in 2.46.1)
            GetFileNameFromUrl(url, Convert.ToHexStringLower(hashData).Truncate(5)),
            // Uppercase variant (original)
            GetFileNameFromUrl(url, Convert.ToHexString(hashData).Truncate(5)),
        ];
    }
}
