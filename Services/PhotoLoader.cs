using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WarehousePacking.Services;

/// Hands out thumbnails without touching the UI thread: disk reads, downloads
/// and decoding all happen in the background and only the finished frozen
/// image is pushed back, so scrolling and clicking stay responsive while a
/// page of photos arrives.
public sealed class PhotoLoader(Dispatcher dispatcher)
{
    private const int MaxParallelLoads = 4;

    private readonly Dictionary<string, ImageSource> _ready = [];
    private readonly Dictionary<string, List<Action<ImageSource>>> _waiting = [];
    private readonly HashSet<string> _failed = [];
    private readonly SemaphoreSlim _slots = new(MaxParallelLoads, MaxParallelLoads);

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
        if (_waiting.TryGetValue(key, out var pending))
        {
            pending.Add(apply);
            return;
        }
        _waiting[key] = [apply];
        _ = LoadAsync(raw, size, key);
    }

    private async Task LoadAsync(string url, int size, string key)
    {
        ImageSource? image = null;
        await _slots.WaitAsync();
        try
        {
            image = await Task.Run(async () =>
            {
                var data = ImageCache.GetCached(url) ?? await ImageCache.FetchAsync(url);
                return data is null ? null : Decode(data, size);
            });
        }
        catch { }
        finally { _slots.Release(); }
        await dispatcher.InvokeAsync(() => Publish(key, image), DispatcherPriority.Background);
    }

    private void Publish(string key, ImageSource? image)
    {
        if (!_waiting.Remove(key, out var callbacks))
            return;
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

    /// Frozen so it can be produced off the UI thread and shared between rows.
    public static ImageSource? Decode(byte[] data, int decodeWidth = 0)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
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
