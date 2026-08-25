using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.JellyPlugCacheHeaders;

/// <summary>
/// Runs ahead of Jellyfin's own pipeline (IStartupFilter). For the JellyPlug theme
/// assets and the jellyfin-web bundles it: (1) stamps path-appropriate Cache-Control,
/// (2) answers If-None-Match with 304 against a strong SHA-256 ETag of the identity
/// body, and (3) compresses the body itself at best quality (brotli 11 / gzip 9)
/// instead of letting the server's on-the-fly Fastest-level compression run, caching
/// the compressed bytes per content version. Accept-Encoding and the conditional
/// headers are stripped from the downstream request so the built-in
/// ResponseCompression / static-file middlewares stay out of the way.
/// </summary>
public class CacheHeaderMiddleware
{
    private const string BrandingPath = "/Branding/Css";
    private const string BrandingPathCssExt = "/Branding/Css.css";
    private const string InjectorPrefix = "/JavaScriptInjector/";
    private const string WebPrefix = "/web/";

    private const string RevalidateCacheControl = "public, max-age=0, must-revalidate";
    private const string BrandingCacheControl = "public, max-age=3600";
    private const string VersionedPublicCacheControl = "public, max-age=604800, immutable";
    private const string VersionedPrivateCacheControl = "private, max-age=604800, immutable";

    private const string VaryAcceptEncoding = "Accept-Encoding";

    // /web/ urls are loaded both as no-cors <script> tags and as CORS fetch(), and
    // M63 does not partition its HTTP cache by request mode: without Vary: Origin a
    // cached no-cors entry (no ACAO — the server only emits it when Origin is sent)
    // gets handed to a later fetch() and fails CORS. Same lesson as ShellController.
    private const string VaryAcceptEncodingOrigin = "Accept-Encoding, Origin";

    // Compressing tiny bodies costs more than it saves (private.js is usually near-empty).
    private const int MinCompressLength = 512;

    // Latest compressed variant per "{path}|{encoding}"; replaced when the ETag moves.
    private static readonly ConcurrentDictionary<string, CompressedVariant> s_variants = new(StringComparer.OrdinalIgnoreCase);

    // How index.html references the entry bundle; the same compilation hash is the
    // query string on every other bundle request.
    private static readonly Regex s_mainBundleHashPattern = new Regex(
        "main\\.jellyfin\\.bundle\\.js\\?([0-9a-fA-F]{8,64})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly RequestDelegate m_next;

    private readonly ILogger<CacheHeaderMiddleware> m_logger;

    private readonly IApplicationPaths m_appPaths;

    private readonly object m_hashLock = new object();

    private string? m_cachedBuildHash;

    private DateTime m_cachedBuildHashStamp;

    public CacheHeaderMiddleware(RequestDelegate next, ILogger<CacheHeaderMiddleware> logger, IApplicationPaths appPaths)
    {
        m_next = next;
        m_logger = logger;
        m_appPaths = appPaths;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method) || !TryGetCacheControl(context.Request, out string cacheControl, out string vary))
        {
            await m_next(context).ConfigureAwait(false);
            return;
        }

        string acceptEncoding = context.Request.Headers[HeaderNames.AcceptEncoding].ToString();
        string ifNoneMatch = context.Request.Headers[HeaderNames.IfNoneMatch].ToString();
        context.Request.Headers.Remove(HeaderNames.AcceptEncoding);

        // The static-file middleware serving /web/ answers conditionals itself with a
        // 304 that carries none of our headers (and an empty body this middleware
        // would then hash). Take the conditional headers off the downstream request
        // and answer If-None-Match here against the strong ETag.
        context.Request.Headers.Remove(HeaderNames.IfNoneMatch);
        context.Request.Headers.Remove(HeaderNames.IfModifiedSince);

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
        context.Response.Headers[HeaderNames.Vary] = vary;

        if (IfNoneMatchSatisfied(ifNoneMatch, etag))
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

    public bool TryGetCacheControl(HttpRequest request, out string cacheControl, out string vary)
    {
        vary = VaryAcceptEncoding;
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

        // jellyfin-web bundles: /web/<name>?<compilation hash>. The hash is a bare
        // hex query — those bytes cannot change without the URL changing, but only
        // when the hash is the one the CURRENT build mints. A stale hash (a client
        // still holding an older index.html) does NOT address the bytes the server
        // would return today, so it gets revalidate-only — an immutable there would
        // outlive the mistake. .html is never matched: index.html is where a new
        // build hash is discovered and must keep the server's no-cache.
        if (value.StartsWith(WebPrefix, StringComparison.OrdinalIgnoreCase)
            && !value.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
            && TryGetBareHexQuery(request.QueryString.Value, out string requestHash))
        {
            string? currentHash = GetCurrentWebBuildHash();
            cacheControl = currentHash is not null
                && string.Equals(requestHash, currentHash, StringComparison.OrdinalIgnoreCase)
                ? VersionedPublicCacheControl
                : RevalidateCacheControl;
            vary = VaryAcceptEncodingOrigin;
            return true;
        }

        cacheControl = string.Empty;
        return false;
    }

    public static bool TryGetBareHexQuery(string? queryString, out string hash)
    {
        hash = string.Empty;
        if (queryString is null || queryString.Length < 9 || queryString.Length > 65 || queryString[0] != '?')
        {
            return false;
        }

        for (int i = 1; i < queryString.Length; i++)
        {
            char c = queryString[i];
            bool isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!isHex)
            {
                return false;
            }
        }

        hash = queryString.Substring(1);
        return true;
    }

    private string? GetCurrentWebBuildHash()
    {
        try
        {
            string webPath = m_appPaths.WebPath;
            if (string.IsNullOrEmpty(webPath))
            {
                return null;
            }

            string indexPath = Path.Combine(webPath, "index.html");
            DateTime stamp = File.GetLastWriteTimeUtc(indexPath);
            lock (m_hashLock)
            {
                if (m_cachedBuildHash is not null && stamp == m_cachedBuildHashStamp)
                {
                    return m_cachedBuildHash;
                }
            }

            Match match = s_mainBundleHashPattern.Match(File.ReadAllText(indexPath));
            if (!match.Success)
            {
                m_logger.LogWarning("JellyPlugCacheHeaders: no main bundle hash found in {IndexPath}; /web/ stays revalidate-only", indexPath);
                return null;
            }

            string hash = match.Groups[1].Value;
            lock (m_hashLock)
            {
                m_cachedBuildHash = hash;
                m_cachedBuildHashStamp = stamp;
            }

            m_logger.LogInformation("JellyPlugCacheHeaders: current /web/ build hash is {Hash}", hash);
            return hash;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            m_logger.LogWarning(e, "JellyPlugCacheHeaders: could not read the /web/ build hash; /web/ stays revalidate-only");
            return null;
        }
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
