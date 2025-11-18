using System;
using System.Collections.Frozen;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Orleans.Dashboard;

internal sealed class EmbeddedAssetProvider
{
    private const string GZipEncodingValue = "gzip";
    private static readonly StringValues GzipEncodingHeader = new(GZipEncodingValue);
    private static readonly Assembly Assembly = typeof(EmbeddedAssetProvider).Assembly;
    private static readonly FileExtensionContentTypeProvider ContentTypeProvider = new();
    private static readonly StringValues CacheControl = new(new CacheControlHeaderValue()
    {
        NoCache = true,
        NoStore = true,
    }.ToString());

    private readonly FrozenDictionary<string, ResourceEntry> _resourceCache;

    public EmbeddedAssetProvider()
    {
        // Build resource cache for all embedded resources
        var resourceNamePrefix = $"{Assembly.GetName().Name}.wwwroot.";
        _resourceCache = Assembly
            .GetManifestResourceNames()
            .Where(p => p.StartsWith(resourceNamePrefix, StringComparison.Ordinal))
            .ToFrozenDictionary(
                p => p[resourceNamePrefix.Length..],
                CreateResourceEntry,
                StringComparer.OrdinalIgnoreCase);
    }

    public IResult ServeAsset(string name, HttpContext context)
    {
        // Embedded resources use dots instead of slashes for directory separators
        var resourceKey = name.Replace('/', '.');

        if (!_resourceCache.TryGetValue(resourceKey, out var entry))
        {
            return Results.NotFound();
        }

        // Check if client has cached version
        if (context.Request.Headers.IfNoneMatch == entry.ETag)
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        byte[] contents;
        var response = context.Response;
        response.Headers.CacheControl = CacheControl;
        if (entry.CompressedContent is not null && IsGZipAccepted(context.Request))
        {
            response.Headers.ContentEncoding = GzipEncodingHeader;
            contents = entry.CompressedContent;
        }
        else
        {
            contents = entry.DecompressedContent;
        }

        return Results.Bytes(
            contents,
            contentType: entry.ContentType,
            entityTag: new EntityTagHeaderValue(entry.ETag));
    }

    private static bool IsGZipAccepted(HttpRequest httpRequest)
    {
        if (httpRequest.GetTypedHeaders().AcceptEncoding is not { Count: > 0 } acceptEncoding)
        {
            return false;
        }

        for (int i = 0; i < acceptEncoding.Count; i++)
        {
            var encoding = acceptEncoding[i];
            if (encoding.Quality is not 0 &&
                string.Equals(encoding.Value.Value, GZipEncodingValue, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static ResourceEntry CreateResourceEntry(string fullResourceName)
    {
        using var resourceStream = Assembly.GetManifestResourceStream(fullResourceName) ?? throw new FileNotFoundException($"Embedded resource not found: {fullResourceName}");
        using var decompressedContent = new MemoryStream();
        resourceStream.CopyTo(decompressedContent);
        var decompressedArray = decompressedContent.ToArray();

        // Compress the content
        using var compressedContent = new MemoryStream();
        using (var gzip = new GZipStream(compressedContent, CompressionMode.Compress, leaveOpen: true))
        {
            gzip.Write(decompressedArray);
        }

        // Only use compression if it actually reduces size
        byte[] compressedArray = compressedContent.Length < decompressedArray.Length
            ? compressedContent.ToArray()
            : null;

        var hash = SHA256.HashData(compressedArray ?? decompressedArray);
        var eTag = $"\"{Convert.ToBase64String(hash)}\"";

        // Extract just the file name with extension for content type detection
        var fileName = fullResourceName.Split('.').TakeLast(2).FirstOrDefault() + "." + fullResourceName.Split('.').Last();
        var contentType = ContentTypeProvider.TryGetContentType(fileName, out var ct)
            ? ct
            : "application/octet-stream";

        return new ResourceEntry(decompressedArray, compressedArray, eTag, contentType);
    }

    private sealed class ResourceEntry(byte[] decompressedContent, byte[] compressedContent, string eTag, string contentType)
    {
        public byte[] CompressedContent { get; } = compressedContent;
        public string ContentType { get; } = contentType;
        public byte[] DecompressedContent { get; } = decompressedContent;
        public string ETag { get; } = eTag;
    }
}
