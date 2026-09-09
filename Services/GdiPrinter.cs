using System.Drawing;
using System.Drawing.Printing;
using System.IO;
using PDFtoImage;
using SkiaSharp;

namespace WarehousePacking.Services;

public static class GdiPrinter
{
    private static readonly object Gate = new();

    public static IReadOnlyList<string> InstalledPrinters()
    {
        var names = new List<string>();
        foreach (string name in PrinterSettings.InstalledPrinters)
            names.Add(name);
        names.Sort(StringComparer.CurrentCultureIgnoreCase);
        return names;
    }

    public static void PrintPdf(byte[] pdfBytes, PrintProfile profile, int copies = 1)
    {
        if (pdfBytes is null || pdfBytes.Length == 0)
            throw new InvalidOperationException("Пустой PDF");
        var options = PrintOptions.Parse(profile.Settings);
        var dpi = options.Paper == "a4" ? 200 : 300;
        var pages = Rasterize(pdfBytes, dpi);
        try
        {
            lock (Gate)
                PrintPages(pages, profile.Printer, Math.Clamp(copies, 1, 9999), options);
        }
        finally
        {
            foreach (var (bmp, _) in pages)
                bmp.Dispose();
        }
    }

    public static void PrintBitmaps(IEnumerable<Bitmap> images, PrintProfile profile, int copies = 1)
    {
        var options = PrintOptions.Parse(profile.Settings);
        var pages = images.Select(img => (img, (img.Width * 72.0 / 300.0, img.Height * 72.0 / 300.0))).ToList();
        lock (Gate)
            PrintPages(pages, profile.Printer, Math.Clamp(copies, 1, 9999), options);
    }

    private static List<(Bitmap Bitmap, (double W, double H) Pts)> Rasterize(byte[] pdf, int dpi)
    {
        using var stream = new MemoryStream(pdf, writable: false);
        var pages = new List<(Bitmap, (double, double))>();
        var index = 0;
        foreach (var sk in Conversion.ToImages(stream, options: new RenderOptions { Dpi = dpi }))
        {
            using (sk)
            {
                var bmp = ToBitmap(sk);
                pages.Add((bmp, (bmp.Width * 72.0 / dpi, bmp.Height * 72.0 / dpi)));
            }
            index++;
        }
        if (pages.Count == 0)
            throw new InvalidOperationException("Empty PDF");
        return pages;
    }

    private static Bitmap ToBitmap(SKBitmap sk)
    {
        using var image = SKImage.FromBitmap(sk);
        using var data = image.Encode(SKEncodedImageFormat.Png, 92);
        using var ms = new MemoryStream();
        data.SaveTo(ms);
        ms.Position = 0;
        return new Bitmap(ms);
    }

    private static void PrintPages(
        List<(Bitmap Bitmap, (double W, double H) Pts)> pages,
        string printer,
        int copies,
        (bool Landscape, bool Noscale, string Paper, double? WidthMm, double? HeightMm) options)
    {
        using var doc = new PrintDocument();
        doc.DocumentName = "Warehouse packing";
        doc.PrinterSettings.PrinterName = ResolvePrinter(printer);
        doc.PrinterSettings.Copies = 1;
        ApplyPaper(doc, options);

        var pageIndex = 0;
        var copyIndex = 0;
        doc.PrintPage += (_, e) =>
        {
            var (image, pts) = pages[pageIndex];
            var area = e.PageBounds;
            if (e.Graphics is null)
                return;
            var dest = DestRect(image, pts, area.Size, options.Noscale, e.Graphics.DpiX, e.Graphics.DpiY);
            e.Graphics.DrawImage(image, dest);
            pageIndex++;
            if (pageIndex < pages.Count)
            {
                e.HasMorePages = true;
                return;
            }
            pageIndex = 0;
            copyIndex++;
            e.HasMorePages = copyIndex < copies;
        };
        doc.Print();
    }

    private static string ResolvePrinter(string name)
    {
        var wanted = (name ?? "").Trim();
        if (wanted.Length == 0)
            return new PrinterSettings().PrinterName;
        foreach (string item in PrinterSettings.InstalledPrinters)
        {
            if (string.Equals(item, wanted, StringComparison.OrdinalIgnoreCase))
                return item;
        }
        throw new FileNotFoundException($"Принтер не найден: {name}");
    }

    private static void ApplyPaper(
        PrintDocument doc,
        (bool Landscape, bool Noscale, string Paper, double? WidthMm, double? HeightMm) options)
    {
        doc.DefaultPageSettings.Landscape = options.Landscape;
        if (options.Paper == "a4")
        {
            doc.DefaultPageSettings.PaperSize = new PaperSize("A4", 827, 1169);
            return;
        }
        if (options.Paper == "custom" && options.WidthMm is > 0 && options.HeightMm is > 0)
        {
            var w = Math.Max(1, (int)Math.Round(options.WidthMm.Value / 25.4 * 100));
            var h = Math.Max(1, (int)Math.Round(options.HeightMm.Value / 25.4 * 100));
            if (options.Landscape && h > w)
                (w, h) = (h, w);
            doc.DefaultPageSettings.PaperSize = new PaperSize("Custom", w, h);
        }
    }

    private static Rectangle DestRect(Bitmap image, (double W, double H) pts, Size area, bool noscale, float dpiX, float dpiY)
    {
        int drawW, drawH;
        if (noscale && pts.W > 0 && pts.H > 0)
        {
            drawW = (int)Math.Round(pts.W / 72.0 * dpiX);
            drawH = (int)Math.Round(pts.H / 72.0 * dpiY);
        }
        else
        {
            var scale = Math.Min(area.Width / (double)image.Width, area.Height / (double)image.Height);
            drawW = Math.Max(1, (int)Math.Round(image.Width * scale));
            drawH = Math.Max(1, (int)Math.Round(image.Height * scale));
        }
        if (drawW > area.Width || drawH > area.Height)
        {
            var scale = Math.Min(area.Width / (double)Math.Max(drawW, 1), area.Height / (double)Math.Max(drawH, 1));
            drawW = Math.Max(1, (int)Math.Round(drawW * scale));
            drawH = Math.Max(1, (int)Math.Round(drawH * scale));
        }
        var x = Math.Max(0, (area.Width - drawW) / 2);
        var y = Math.Max(0, (area.Height - drawH) / 2);
        return new Rectangle(x, y, drawW, drawH);
    }
}
