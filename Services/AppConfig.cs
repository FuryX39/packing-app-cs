using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace WarehousePacking.Services;

public sealed class AppConfig
{
    public string ServerUrl { get; set; } = "";
    public string ApiUrl { get; set; } = "";
    public string Printer { get; set; } = "";
    public string PrintSettings { get; set; } = "noscale,portrait,disable-auto-rotation,paper=47mm x 25mm";
    public string PrinterA4 { get; set; } = "";
    public string PrintSettingsA4 { get; set; } = "paper=A4,portrait";
    public string PrinterLabel { get; set; } = "";
    public string PrintSettingsLabel { get; set; } = "noscale,portrait,disable-auto-rotation,paper=47mm x 25mm";
    public string RefreshSeconds { get; set; } = "30";
    public string FbsSkipMpConfirm { get; set; } = "0";

    public string LabelPrinter => string.IsNullOrWhiteSpace(PrinterLabel) ? Printer : PrinterLabel;
    public string LabelSettings => string.IsNullOrWhiteSpace(PrintSettingsLabel) ? PrintSettings : PrintSettingsLabel;

    public int RefreshMs
    {
        get
        {
            if (!int.TryParse(RefreshSeconds, out var sec) || sec < 5)
                sec = 30;
            return sec * 1000;
        }
    }

    public bool SkipMpConfirm
    {
        get
        {
            var raw = (FbsSkipMpConfirm ?? "").Trim().ToLowerInvariant();
            return raw is "1" or "true" or "yes" or "on";
        }
        set => FbsSkipMpConfirm = value ? "1" : "0";
    }

    public static string ConfigPath
    {
        get
        {
            foreach (var dir in CandidateDirs())
            {
                var path = Path.Combine(dir, "config.env");
                if (File.Exists(path))
                    return path;
            }
            return Path.Combine(AppContext.BaseDirectory, "config.env");
        }
    }

    public static AppConfig Load()
    {
        var cfg = new AppConfig();
        var path = ConfigPath;
        if (!File.Exists(path))
        {
            var example = Path.Combine(Path.GetDirectoryName(path) ?? AppContext.BaseDirectory, "config.env.example");
            if (File.Exists(example))
                path = example;
        }
        if (!File.Exists(path))
            return cfg;

        foreach (var rawLine in File.ReadAllLines(path, Encoding.UTF8))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || !line.Contains('='))
                continue;
            var idx = line.IndexOf('=');
            var key = line[..idx].Trim();
            var value = Unquote(line[(idx + 1)..].Trim());
            switch (key)
            {
                case "WAREHOUSE_SERVER_URL": cfg.ServerUrl = value.TrimEnd('/'); break;
                case "WAREHOUSE_API_URL": cfg.ApiUrl = value.TrimEnd('/'); break;
                case "BARCODE_PRINT_PRINTER": cfg.Printer = value; break;
                case "BARCODE_PRINT_SETTINGS": cfg.PrintSettings = value; break;
                case "BARCODE_PRINT_PRINTER_A4": cfg.PrinterA4 = value; break;
                case "BARCODE_PRINT_SETTINGS_A4": cfg.PrintSettingsA4 = value; break;
                case "BARCODE_PRINT_PRINTER_LABEL": cfg.PrinterLabel = value; break;
                case "BARCODE_PRINT_SETTINGS_LABEL": cfg.PrintSettingsLabel = value; break;
                case "REFRESH_SECONDS": cfg.RefreshSeconds = value; break;
                case "FBS_SKIP_MP_CONFIRM": cfg.FbsSkipMpConfirm = value; break;
            }
        }
        if (string.IsNullOrWhiteSpace(cfg.PrinterLabel))
            cfg.PrinterLabel = cfg.Printer;
        if (string.IsNullOrWhiteSpace(cfg.PrintSettingsLabel))
            cfg.PrintSettingsLabel = cfg.PrintSettings;
        return cfg;
    }

    public void Save()
    {
        var path = ConfigPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var lines = new[]
        {
            "# Warehouse Packing App config",
            "# Saved from application settings.",
            "",
            $"WAREHOUSE_SERVER_URL={Quote(ServerUrl.TrimEnd('/'))}",
            $"WAREHOUSE_API_URL={Quote(ApiUrl.TrimEnd('/'))}",
            $"BARCODE_PRINT_PRINTER={Quote(Printer)}",
            $"BARCODE_PRINT_SETTINGS={Quote(PrintSettings)}",
            $"BARCODE_PRINT_PRINTER_A4={Quote(PrinterA4)}",
            $"BARCODE_PRINT_SETTINGS_A4={Quote(PrintSettingsA4)}",
            $"BARCODE_PRINT_PRINTER_LABEL={Quote(LabelPrinter)}",
            $"BARCODE_PRINT_SETTINGS_LABEL={Quote(LabelSettings)}",
            $"REFRESH_SECONDS={Quote(RefreshSeconds)}",
            $"FBS_SKIP_MP_CONFIRM={Quote(FbsSkipMpConfirm)}",
            "",
        };
        File.WriteAllText(path, string.Join('\n', lines), new UTF8Encoding(false));
    }

    public PrintProfile LabelProfile() => new(LabelPrinter, LabelSettings);
    public PrintProfile A4Profile() => new(PrinterA4, string.IsNullOrWhiteSpace(PrintSettingsA4) ? "paper=A4,portrait" : PrintSettingsA4);

    private static IEnumerable<string> CandidateDirs()
    {
        yield return AppContext.BaseDirectory;
        yield return Directory.GetCurrentDirectory();
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 5 && dir != null; i++, dir = dir.Parent)
            yield return dir.FullName;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '"')
        {
            var inner = value[1..].TrimEnd('"');
            return inner.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\n", "\n");
        }
        return value;
    }

    private static string Quote(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        if (value.IndexOfAny(['"', '#', '\n', '\r']) >= 0 || value.StartsWith(' ') || value.EndsWith(' '))
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "") + "\"";
        return value;
    }
}

public readonly record struct PrintProfile(string Printer, string Settings);

public static class PrintOptions
{
    private static readonly Regex PaperMm = new(@"paper\s*=\s*([\d.]+)\s*mm\s*x\s*([\d.]+)\s*mm", RegexOptions.IgnoreCase);

    public static (bool Landscape, bool Noscale, string Paper, double? WidthMm, double? HeightMm) Parse(string settings)
    {
        var raw = settings ?? "";
        var folded = raw.ToLowerInvariant();
        var landscape = folded.Contains("landscape");
        var noscale = folded.Contains("noscale");
        var paper = "default";
        double? w = null, h = null;
        if (Regex.IsMatch(folded, @"paper\s*=\s*a4\b"))
        {
            paper = "a4";
            w = 210;
            h = 297;
        }
        else
        {
            var m = PaperMm.Match(raw);
            if (m.Success)
            {
                paper = "custom";
                w = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                h = double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        return (landscape, noscale, paper, w, h);
    }

    public static (double WidthMm, double HeightMm) LabelSizeMm(string settings)
    {
        var parsed = Parse(settings);
        if (parsed.Paper == "custom" && parsed.WidthMm is > 0 && parsed.HeightMm is > 0)
            return (parsed.WidthMm.Value, parsed.HeightMm.Value);
        return (47, 25);
    }
}
