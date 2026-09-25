using System.Security.Cryptography;

namespace Keel.Infrastructure.Attachments;

/// <summary>File-level helpers of the attachment store (F-TXN-8): hashing copies, MIME types and safe names.</summary>
internal static class AttachmentFiles
{
    private static readonly Dictionary<string, string> MimeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".heic"] = "image/heic",
        [".heif"] = "image/heif",
        [".tif"] = "image/tiff",
        [".tiff"] = "image/tiff",
        [".bmp"] = "image/bmp",
        [".svg"] = "image/svg+xml",
        [".txt"] = "text/plain",
        [".csv"] = "text/csv",
        [".html"] = "text/html",
        [".htm"] = "text/html",
        [".eml"] = "message/rfc822",
        [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"] = "application/vnd.ms-excel",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".odt"] = "application/vnd.oasis.opendocument.text",
        [".ods"] = "application/vnd.oasis.opendocument.spreadsheet",
        [".zip"] = "application/zip",
    };

    // Characters no file system accepts in a name (the Windows set, used on every OS so names travel).
    private static readonly char[] Invalid = [.. Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*'])];

    /// <summary>Longest original file name kept (the column holds 260).</summary>
    public const int MaxFileNameLength = 260;

    /// <summary>The MIME type for a file name's extension, or <c>application/octet-stream</c>.</summary>
    public static string MimeTypeFor(string fileName) =>
        MimeTypes.TryGetValue(Path.GetExtension(fileName), out var type) ? type : "application/octet-stream";

    /// <summary>Whether <paramref name="name"/> is a stored file's name (64 lower-case hex characters).</summary>
    public static bool IsHashName(string name) => name.Length == 64 && name.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>The original name as stored: the file name part, cut to <see cref="MaxFileNameLength"/> keeping the extension.</summary>
    public static string StoredName(string path)
    {
        var name = Path.GetFileName(path.Trim());
        if (name.Length <= MaxFileNameLength)
        {
            return name;
        }

        var extension = Path.GetExtension(name);
        extension = extension.Length > 20 ? string.Empty : extension;
        return name[..(MaxFileNameLength - extension.Length)] + extension;
    }

    /// <summary>A name safe on every file system for an opened copy; falls back to <paramref name="fallback"/>.</summary>
    public static string SafeName(string fileName, string fallback)
    {
        var name = new string(Path.GetFileName(fileName).Select(c => Invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (name.Length == 0 || name is "." or "..")
        {
            name = fallback;
        }

        if (name.Length > 120)
        {
            var extension = Path.GetExtension(name);
            extension = extension.Length > 20 ? string.Empty : extension;
            name = name[..(120 - extension.Length)] + extension;
        }

        return name;
    }

    /// <summary>Copies <paramref name="source"/> to <paramref name="destination"/> and returns the lower-case hex SHA-256 of the bytes copied.</summary>
    public static async Task<string> CopyWithHashAsync(string source, string destination, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        await using (input.ConfigureAwait(false))
        {
            var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await using (output.ConfigureAwait(false))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
            }
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
