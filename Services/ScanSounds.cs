using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;

namespace WarehousePacking.Services;

/// <summary>Звуковой отклик сканера: успех / ошибка (WAV через MediaPlayer, вызывать из UI-потока).</summary>
internal static class ScanSounds
{
    private const int SampleRate = 44100;

    private static MediaPlayer? _ok;
    private static MediaPlayer? _error;
    private static bool _initFailed;
    private static readonly SemaphoreSlim SpeechGate = new(1, 1);

    public static void Init()
    {
        if (_ok is not null || _initFailed) return;
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "WarehousePacking");
            Directory.CreateDirectory(dir);
            _ok = CreatePlayer(Path.Combine(dir, "scan_ok.wav"), [(1400, 110, 0.0)]);
            _error = CreatePlayer(Path.Combine(dir, "scan_error.wav"), [(520, 180, 0.0), (330, 320, 70)]);
        }
        catch
        {
            _initFailed = true;
            _ok = null;
            _error = null;
        }
    }

    public static void Ok() => Play(_ok);

    public static void Error() => Play(_error);

    public static void OkWithQuantity(int quantity)
    {
        Ok();
        if (quantity <= 0) return;
        _ = Task.Run(async () =>
        {
            await SpeechGate.WaitAsync();
            try
            {
                await Task.Delay(160);
                SpeakQuantity(quantity);
            }
            finally
            {
                SpeechGate.Release();
            }
        });
    }

    private static void Play(MediaPlayer? player)
    {
        Init();
        if (player is null) return;
        try
        {
            player.Stop();
            player.Position = TimeSpan.Zero;
            player.Play();
        }
        catch
        {
            // Нет аудиоустройства — пропускаем.
        }
    }

    private static void SpeakQuantity(int quantity)
    {
        object? voice = null;
        object? voices = null;
        object? token = null;
        try
        {
            var voiceType = Type.GetTypeFromProgID("SAPI.SpVoice");
            if (voiceType is null) return;
            voice = Activator.CreateInstance(voiceType);
            if (voice is null) return;
            dynamic speaker = voice;
            try
            {
                voices = speaker.GetVoices("Language=419", "");
                dynamic installed = voices;
                if ((int)installed.Count > 0)
                {
                    token = installed.Item(0);
                    speaker.Voice = token;
                }
            }
            catch
            {
                // Русский голос не установлен — используется системный голос по умолчанию.
            }
            speaker.Rate = 1;
            speaker.Volume = 100;
            speaker.Speak(quantity.ToString(), 0);
        }
        catch
        {
            // SAPI недоступен — короткий сигнал успеха уже прозвучал.
        }
        finally
        {
            ReleaseCom(token);
            ReleaseCom(voices);
            ReleaseCom(voice);
        }
    }

    private static void ReleaseCom(object? value)
    {
        if (value is null || !Marshal.IsComObject(value)) return;
        try { Marshal.FinalReleaseComObject(value); }
        catch { }
    }

    private static MediaPlayer CreatePlayer(string path, (int Hz, int Ms, double GapBeforeMs)[] tones)
    {
        File.WriteAllBytes(path, BuildWav(tones));
        var player = new MediaPlayer { Volume = 1.0 };
        player.Open(new Uri(path));
        return player;
    }

    private static byte[] BuildWav((int Hz, int Ms, double GapBeforeMs)[] tones)
    {
        var samples = new List<short>();
        foreach (var (hz, ms, gap) in tones)
        {
            samples.AddRange(Enumerable.Repeat((short)0, (int)(SampleRate * gap / 1000)));
            var count = SampleRate * ms / 1000;
            var fade = Math.Min(count / 4, SampleRate * 8 / 1000);
            for (var i = 0; i < count; i++)
            {
                var env = 1.0;
                if (i < fade) env = i / (double)fade;
                else if (i > count - fade) env = (count - i) / (double)fade;
                var value = Math.Sin(2 * Math.PI * hz * i / SampleRate) * env * 0.8;
                samples.Add((short)(value * short.MaxValue));
            }
        }
        samples.AddRange(Enumerable.Repeat((short)0, SampleRate * 30 / 1000));

        using var ms2 = new MemoryStream();
        using var w = new BinaryWriter(ms2);
        var dataBytes = samples.Count * 2;
        w.Write("RIFF"u8.ToArray());
        w.Write(36 + dataBytes);
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(SampleRate);
        w.Write(SampleRate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8.ToArray());
        w.Write(dataBytes);
        foreach (var s in samples) w.Write(s);
        w.Flush();
        return ms2.ToArray();
    }
}
