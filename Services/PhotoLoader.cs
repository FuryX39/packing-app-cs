using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WarehousePacking.Services;

/// Hands out thumbnails without rebuilding grids: a cached image is applied
/// immediately, a missing one is downloaded once per URL and then pushed to
/// every caller that asked for it.
public sealed class PhotoLoader(Dispatcher dispatcher)
{
    private const int MaxParallelFetches = 6;

    private readonly Dictionary<string, ImageSource> _ready = [];
    private readonly Dictionary<string, List<Action<ImageSource>>> _waiting = [];
    private readonly HashSet<string> _failed = [];
    private readonly SemaphoreSlim _slots = new(MaxParallelFetches, MaxParallelFetches);

    public void Load(string? url, int size, Action<ImageSource> apply)
    {
        var raw = (url ?? "").Trim();
        if (raw.Length == 0)
            return;
        var key = $"{size}:{raw}";
        if (_ready.TryGetValue(key, out var image))
        {
            apply(image);
            return;
        }
        if (_failed.Contains(key))
            return;
        var cached = ImageCache.GetCached(raw);
        if (cached is not null)
        {
            var decoded = Decode(cached, size);
            if (decoded is null)
                _failed.Add(key);
            else
            {
                _ready[key] = decoded;
                apply(decoded);
            }
            return;
        }
        if (_waiting.TryGetValue(key, out var pending))
        {
            pending.Add(apply);
            return;
        }
        _waiting[key] = [apply];
        _ = FetchAsync(raw, size, key);
    }

    private async Task FetchAsync(string url, int size, string key)
    {
        byte[]? data = null;
        await _slots.WaitAsync();
        try { data = await ImageCache.FetchAsync(url); }
        catch { }
        finally { _slots.Release(); }
        await dispatcher.InvokeAsync(() => Publish(key, size, data));
    }

    private void Publish(string key, int size, byte[]? data)
    {
        if (!_waiting.Remove(key, out var callbacks))
            return;
        var image = data is null ? null : Decode(data, size);
        if (image is null)
        {
            _failed.Add(key);
            return;
        }
        _ready[key] = image;
        foreach (var apply in callbacks)
        {
            try { apply(image); }
            catch { }
        }
    }

    public static ImageSource? Decode(byte[] data, int decodeWidth = 0)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(data);
            if (decodeWidth > 0)
                bmp.DecodePixelWidth = decodeWidth;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }
}
