using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace WarehousePacking.Services;

public static class ImageCache
{
    private static readonly object Gate = new();
    private static readonly HashSet<string> Inflight = [];
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public static string Root => Path.Combine(AppContext.BaseDirectory, "cache", "fbs_images");

    public static string PathFor(string url)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();
        return Path.Combine(Root, hash);
    }

    public static byte[]? GetCached(string url)
    {
        var raw = (url ?? "").Trim();
        if (raw.Length == 0) return null;
        var path = PathFor(raw);
        if (!File.Exists(path)) return null;
        try
        {
            var data = File.ReadAllBytes(path);
            return data.Length >= 24 ? data : null;
        }
        catch { return null; }
    }

    public static bool BeginFetch(string url)
    {
        var raw = (url ?? "").Trim();
        if (!raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return false;
        if (GetCached(raw) != null)
            return false;
        lock (Gate)
        {
            if (!Inflight.Add(raw))
                return false;
        }
        return true;
    }

    public static async Task<byte[]?> FetchAsync(string url)
    {
        var raw = (url ?? "").Trim();
        var cached = GetCached(raw);
        if (cached != null)
            return cached;
        if (!raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, raw);
            req.Headers.UserAgent.ParseAdd("warehouse-packing-app");
            using var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
                return null;
            var data = await resp.Content.ReadAsByteArrayAsync();
            if (data.Length == 0 || data.Length > 2_000_000)
                return null;
            Directory.CreateDirectory(Root);
            var path = PathFor(raw);
            var tmp = path + ".tmp";
            await File.WriteAllBytesAsync(tmp, data);
            File.Move(tmp, path, overwrite: true);
            return data;
        }
        catch { return null; }
        finally
        {
            lock (Gate) Inflight.Remove(raw);
        }
    }
}
