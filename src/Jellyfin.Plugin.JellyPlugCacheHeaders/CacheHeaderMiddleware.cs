using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.JellyPlugCacheHeaders;

/// <summary>
/// Runs ahead of Jellyfin's own pipeline (IStartupFilter). For the JellyPlug theme
/// assets it: (1) stamps path-appropriate Cache-Control, (2) answers If-None-Match
/// with 304 against a strong SHA-256 ETag of the identity body, and (3) compresses
/// the body itself at best quality (brotli 11 / gzip 9) instead of letting the
/// server's on-the-fly Fastest-level compression run, caching the compressed bytes
/// per content version. Accept-Encoding is stripped from the downstream request so
/// the built-in ResponseCompression middleware stays out of the way.
/// </summary>
public class CacheHeaderMiddleware
{
    private const string BrandingPath = "/Branding/Css";
    private const string BrandingPathCssExt = "/Branding/Css.css";
    private const string InjectorPrefix = "/JavaScriptInjector/";

    private const string RevalidateCacheControl = "public, max-age=0, must-revalidate";
    private const string BrandingCacheControl = "public, max-age=3600";
    private const string VersionedPublicCacheControl = "public, max-age=604800, immutable";
    private const string VersionedPrivateCacheControl = "private, max-age=604800, immutable";

    // Compressing tiny bodies costs more than it saves (private.js is usually near-empty).
    private const int MinCompressLength = 512;

    // Latest compressed variant per "{path}|{encoding}"; replaced when the ETag moves.
    private static readonly ConcurrentDictionary<string, CompressedVariant> s_variants = new(StringComparer.OrdinalIgnoreCase);

    private readonly RequestDelegate m_next;

    private readonly ILogger<CacheHeaderMiddleware> m_logger;

    public CacheHeaderMiddleware(RequestDelegate next, ILogger<CacheHeaderMiddleware> logger)
    {
        m_next = next;
        m_logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method) || !TryGetCacheControl(context.Request, out string cacheControl))
        {
            await m_next(context).ConfigureAwait(false);
            return;
        }

        string acceptEncoding = context.Request.Headers[HeaderNames.AcceptEncoding].ToString();
        context.Request.Headers.Remove(HeaderNames.AcceptEncoding);

        Stream originalBody = context.Response.Body;
        using MemoryStream buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await m_next(context).ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        byte[] identity = buffer.GetBuffer();
        int identityLength = (int)buffer.Length;

        // Zero-length 200s still get the caching headers (private.js is empty when no
        // script requires authentication) — otherwise the TV revalidates it every boot.
        bool alreadyEncoded = !string.IsNullOrEmpty(context.Response.Headers[HeaderNames.ContentEncoding].ToString());
        if (context.Response.StatusCode != StatusCodes.Status200OK || alreadyEncoded)
        {
            await originalBody.WriteAsync(identity.AsMemory(0, identityLength)).ConfigureAwait(false);
            return;
        }

        string etag = ComputeETag(identity, identityLength);
        context.Response.Headers[HeaderNames.ETag] = etag;
        context.Response.Headers[HeaderNames.CacheControl] = cacheControl;
        context.Response.Headers[HeaderNames.Vary] = HeaderNames.AcceptEncoding;

        if (IfNoneMatchSatisfied(context.Request.Headers[HeaderNames.IfNoneMatch], etag))
        {
            context.Response.StatusCode = StatusCodes.Status304NotModified;
            context.Response.ContentLength = null;
            context.Response.Headers.Remove(HeaderNames.ContentType);
            context.Response.Headers.Remove(HeaderNames.ContentLength);
            return;
        }

        byte[] body = identity;
        int bodyLength = identityLength;
        if (identityLength >= MinCompressLength)
        {
            string? encoding = PickEncoding(acceptEncoding);
            if (encoding is not null)
            {
                byte[] compressed = GetOrCompress(context.Request.Path.Value ?? string.Empty, encoding, etag, identity, identityLength);
                if (compressed.Length < identityLength)
                {
                    context.Response.Headers[HeaderNames.ContentEncoding] = encoding;
                    body = compressed;
                    bodyLength = compressed.Length;
                }
            }
        }

        context.Response.ContentLength = bodyLength;
        await originalBody.WriteAsync(body.AsMemory(0, bodyLength)).ConfigureAwait(false);
    }

    public static bool TryGetCacheControl(HttpRequest request, out string cacheControl)
    {
        PathString path = request.Path;
        if (path.Equals(BrandingPath, StringComparison.OrdinalIgnoreCase)
            || path.Equals(BrandingPathCssExt, StringComparison.OrdinalIgnoreCase))
        {
            // jellyfin-web requests this URL with no cache-buster (SDK: new URL("/Branding/Css", base)),
            // so a modest TTL + ETag revalidation is the safe ceiling.
            cacheControl = BrandingCacheControl;
            return true;
        }

        string value = path.Value ?? string.Empty;
        if (value.StartsWith(InjectorPrefix, StringComparison.OrdinalIgnoreCase)
            && value.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
        {
            if (request.Query.ContainsKey("v"))
            {
                // The injector URL is version-busted (?v= bumps on every config save),
                // so a long TTL can never serve stale content. private.js is per-user:
                // keep it out of shared caches.
                cacheControl = value.EndsWith("/private.js", StringComparison.OrdinalIgnoreCase)
                    ? VersionedPrivateCacheControl
                    : VersionedPublicCacheControl;
            }
            else
            {
                cacheControl = RevalidateCacheControl;
            }

            return true;
        }

        cacheControl = string.Empty;
        return false;
    }

    public static string? PickEncoding(string acceptEncoding)
    {
        if (string.IsNullOrWhiteSpace(acceptEncoding))
        {
            return null;
        }

        bool br = false;
        bool gzip = false;
        foreach (string part in acceptEncoding.Split(','))
        {
            string[] pieces = part.Split(';');
            string token = pieces[0].Trim();
            bool refused = false;
            for (int i = 1; i < pieces.Length; i++)
            {
                string q = pieces[i].Trim();
                if (q.StartsWith("q=", StringComparison.OrdinalIgnoreCase)
                    && double.TryParse(q.AsSpan(2), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double qValue)
                    && qValue <= 0)
                {
                    refused = true;
                }
            }

            if (refused)
            {
                continue;
            }

            if (string.Equals(token, "br", StringComparison.OrdinalIgnoreCase))
            {
                br = true;
            }
            else if (string.Equals(token, "gzip", StringComparison.OrdinalIgnoreCase))
            {
                gzip = true;
            }
        }

        if (br)
        {
            return "br";
        }

        if (gzip)
        {
            return "gzip";
        }

        return null;
    }

    private byte[] GetOrCompress(string path, string encoding, string etag, byte[] identity, int identityLength)
    {
        string key = path + "|" + encoding;
        if (s_variants.TryGetValue(key, out CompressedVariant? cached) && cached.ETag == etag)
        {
            return cached.Bytes;
        }

        byte[] compressed = Compress(encoding, identity, identityLength);
        s_variants[key] = new CompressedVariant(etag, compressed);
        m_logger.LogInformation(
            "JellyPlugCacheHeaders: compressed {Path} as {Encoding}: {From} -> {To} bytes",
            path,
            encoding,
            identityLength,
            compressed.Length);
        return compressed;
    }

    public static byte[] Compress(string encoding, byte[] bytes, int length)
    {
        using MemoryStream output = new MemoryStream();
        if (encoding == "br")
        {
            using (BrotliStream brotli = new BrotliStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                brotli.Write(bytes, 0, length);
            }
        }
        else
        {
            using (GZipStream gz = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                gz.Write(bytes, 0, length);
            }
        }

        return output.ToArray();
    }

    public static string ComputeETag(byte[] bytes, int length)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(bytes.AsSpan(0, length), hash);
        return "\"" + Convert.ToHexString(hash).ToLowerInvariant() + "\"";
    }

    public static bool IfNoneMatchSatisfied(string? ifNoneMatch, string etag)
    {
        if (string.IsNullOrWhiteSpace(ifNoneMatch))
        {
            return false;
        }

        string[] candidates = ifNoneMatch.Split(',');
        for (int i = 0; i < candidates.Length; i++)
        {
            string candidate = candidates[i].Trim();
            if (candidate == "*")
            {
                return true;
            }

            if (candidate.StartsWith("W/", StringComparison.Ordinal))
            {
                candidate = candidate.Substring(2).Trim();
            }

            if (string.Equals(candidate, etag, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class CompressedVariant
    {
        public CompressedVariant(string etag, byte[] bytes)
        {
            ETag = etag;
            Bytes = bytes;
        }

        public string ETag { get; }

        public byte[] Bytes { get; }
    }
}

public class CacheHeaderStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.UseMiddleware<CacheHeaderMiddleware>();
            next(app);
        };
    }
}
