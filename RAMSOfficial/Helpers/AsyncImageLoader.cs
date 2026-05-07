using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;
using RAMSOfficial.Services;

namespace RAMSOfficial.Helpers;

/// <summary>
/// Loads and downsamples employee photos on a background thread so the UI
/// never freezes. Supports both local file paths and URLs with local disk caching.
/// Uses <see cref="BitmapImage.DecodePixelWidth"/> to keep GPU memory low on the kiosk.
/// </summary>
public static class AsyncImageLoader
{
    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    private static readonly string _cacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "RAMSOfficial", "PhotoCache");

    /// <summary>
    /// Load an image from a file path or URL on a thread-pool thread,
    /// downsampled to <paramref name="decodePixelWidth"/> pixels wide.
    /// URLs are downloaded and cached locally for offline access.
    /// Returns <c>null</c> if the source does not exist or cannot be decoded.
    /// The returned <see cref="BitmapImage"/> is frozen and safe to use on the UI thread.
    /// </summary>
    public static async Task<BitmapImage?> LoadAsync(string? path, int decodePixelWidth = 200)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        path = path.Trim();

        // Common website formats may store photo paths as relative URL segments
        // (e.g. "/storage/employees/a.jpg" or "storage/employees/a.jpg").
        // Resolve those to an absolute URL before trying local-file loading.
        if (LooksLikeRelativeWebPath(path))
        {
            var resolvedWebUrl = BuildWebUrlFromRelativePath(path);
            if (!string.IsNullOrWhiteSpace(resolvedWebUrl))
                path = resolvedWebUrl;
        }

        // Handle base64 image payloads (data:image/...;base64,...)
        if (path.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
        {
            return await Task.Run(() => LoadFromBase64(path, decodePixelWidth));
        }

        // Handle URLs — download and cache locally
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return await LoadFromUrlAsync(uri, decodePixelWidth);
        }

        // Handle local file paths (absolute or relative)
        return await Task.Run(() =>
        {
            var resolved = ResolveLocalPath(path);
            return resolved == null ? null : LoadFromFile(resolved, decodePixelWidth);
        });
    }

    /// <summary>
    /// Gets the local cache path for a photo (URL or file path).
    /// Returns the original path for local files, or the cache path for URLs.
    /// Useful for pre-caching photos for offline use.
    /// </summary>
    public static string? GetCachedPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        if (Uri.TryCreate(path, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            var cachePath = GetCacheFilePath(uri);
            return File.Exists(cachePath) ? cachePath : null;
        }

        if (File.Exists(path)) return path;

        return ResolveLocalPath(path);
    }

    private static bool LooksLikeRelativeWebPath(string path)
    {
        var p = path.Trim();
        if (string.IsNullOrEmpty(p)) return false;

        if (p.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return p.StartsWith("/") ||
               p.StartsWith("~/") ||
               p.StartsWith("storage/", StringComparison.OrdinalIgnoreCase) ||
               p.StartsWith("uploads/", StringComparison.OrdinalIgnoreCase) ||
               p.StartsWith("images/", StringComparison.OrdinalIgnoreCase);
    }

    private static string? BuildWebUrlFromRelativePath(string relativePath)
    {
        var normalized = relativePath.Trim().TrimStart('~').TrimStart('/');
        if (string.IsNullOrWhiteSpace(normalized)) return null;

        var configuredBaseUrl = GetConfiguredBaseUrl();
        if (!string.IsNullOrWhiteSpace(configuredBaseUrl))
            return $"{configuredBaseUrl.TrimEnd('/')}/{normalized}";

        return $"http://localhost:8000/{normalized}";
    }

    private static string? GetConfiguredBaseUrl()
    {
        var keys = new[] { "ApiBaseUrl", "WebsiteBaseUrl", "WebBaseUrl", "FrontendBaseUrl" };
        foreach (var key in keys)
        {
            var value = SecureConfigurationService.GetDecryptedValue(key);
            if (string.IsNullOrWhiteSpace(value)) continue;

            if (Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                return uri.ToString().TrimEnd('/');
            }
        }

        return null;
    }

    private static string? ResolveLocalPath(string path)
    {
        try
        {
            if (Path.IsPathRooted(path))
                return File.Exists(path) ? path : null;

            var candidates = new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path),
                Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? string.Empty, path),
                Path.Combine(Environment.CurrentDirectory, path),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), path)
            };

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static BitmapImage? LoadFromBase64(string base64Data, int decodePixelWidth)
    {
        try
        {
            var payload = base64Data;
            var commaIndex = payload.IndexOf(',');
            if (commaIndex >= 0 && commaIndex < payload.Length - 1)
                payload = payload[(commaIndex + 1)..];

            var bytes = Convert.FromBase64String(payload);
            using var stream = new MemoryStream(bytes);

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = decodePixelWidth;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();

            return bitmap;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AsyncImageLoader] Error loading base64 image: {ex.Message}");
            return null;
        }
    }

    private static BitmapImage? LoadFromFile(string path, int decodePixelWidth)
    {
        try
        {
            if (!File.Exists(path))
            {
                Debug.WriteLine($"[AsyncImageLoader] File not found: {path}");
                return null;
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = decodePixelWidth;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze(); // critical — makes it cross-thread safe

            Debug.WriteLine($"[AsyncImageLoader] Loaded {Path.GetFileName(path)} ({bitmap.PixelWidth}x{bitmap.PixelHeight})");
            return bitmap;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AsyncImageLoader] Error loading {path}: {ex.Message}");
            return null;
        }
    }

    private static async Task<BitmapImage?> LoadFromUrlAsync(Uri uri, int decodePixelWidth)
    {
        try
        {
            Directory.CreateDirectory(_cacheDir);
            var cachePath = GetCacheFilePath(uri);

            // Prefer fresh download first to avoid stale historical photos.
            var bytes = await _httpClient.GetByteArrayAsync(uri);
            await File.WriteAllBytesAsync(cachePath, bytes);
            return await Task.Run(() => LoadFromFile(cachePath, decodePixelWidth));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AsyncImageLoader] Error loading URL {uri}: {ex.Message}");

            // Try loading from cache even if download failed (stale cache for offline)
            var cachePath = GetCacheFilePath(uri);
            if (File.Exists(cachePath))
            {
                Debug.WriteLine("[AsyncImageLoader] Using stale cache for offline fallback.");
                return await Task.Run(() => LoadFromFile(cachePath, decodePixelWidth));
            }

            return null;
        }
    }

    private static string GetCacheFilePath(Uri uri)
    {
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(uri.ToString())))[..16];
        return Path.Combine(_cacheDir, $"{hash}.jpg");
    }
}
