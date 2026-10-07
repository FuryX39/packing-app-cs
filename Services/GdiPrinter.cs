using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using PDFtoImage;
using SkiaSharp;

namespace WarehousePacking.Services;

public static class GdiPrinter
{
    private static readonly object Gate = new();
    private const int PrintTimeoutSeconds = 90;

    public static IReadOnlyList<string> InstalledPrinters()
    {
        var names = new List<string>();
        foreach (string name in PrinterSettings.InstalledPrinters)
            names.Add(name);
        names.Sort(StringComparer.CurrentCultureIgnoreCase);
        return names;
    }

    public static void PrintPdf(
        byte[] pdfBytes,
        PrintProfile profile,
        int copies = 1,
        bool rotatePortrait = false)
    {
        PrintPdfs([pdfBytes], profile, copies, rotatePortrait);
    }

    public static void PrintPdfsIsolated(
        IEnumerable<byte[]> pdfs,
        PrintProfile profile,
        int copies = 1)
    {
        var documents = pdfs.Where(item => item is { Length: > 0 }).ToList();
        if (documents.Count == 0)
            throw new InvalidOperationException("Пустой PDF");
        if (PeerPrint.IsRemote(profile.Printer))
        {
            PrintPdfs(documents, profile, copies);
            return;
        }

        var directory = Path.Combine(
            Path.GetTempPath(),
            "WarehousePacking",
            "print_jobs",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var paths = new List<string>();
            for (var index = 0; index < documents.Count; index++)
            {
                var path = Path.Combine(directory, $"{index + 1}.pdf");
                File.WriteAllBytes(path, documents[index]);
                paths.Add(path);
            }
            var requestPath = Path.Combine(directory, "request.json");
            var errorPath = Path.Combine(directory, "error.txt");
            var request = new PrintWorkerRequest
            {
                Printer = profile.Printer,
                Settings = profile.Settings,
                Copies = Math.Clamp(copies, 1, 9999),
                PdfPaths = paths,
            };
            File.WriteAllText(requestPath, JsonSerializer.Serialize(request));

            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable))
                throw new InvalidOperationException("Не найден исполняемый файл программы");
            var start = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            start.ArgumentList.Add("--print-worker");
            start.ArgumentList.Add(requestPath);
            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("Не удалось запустить процесс печати");
            if (!process.WaitForExit(TimeSpan.FromSeconds(PrintTimeoutSeconds)))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw new TimeoutException(
                    $"Принтер не ответил за {PrintTimeoutSeconds} секунд. " +
                    "Проверьте очередь печати и повторите ярлык.");
            }
            if (process.ExitCode != 0)
            {
                var message = File.Exists(errorPath)
                    ? File.ReadAllText(errorPath).Trim()
                    : "Процесс печати завершился с ошибкой";
                throw new InvalidOperationException(message);
            }
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    public static int RunPrintWorker(string requestPath)
    {
        var directory = Path.GetDirectoryName(requestPath) ?? "";
        try
        {
            var request = JsonSerializer.Deserialize<PrintWorkerRequest>(
                File.ReadAllText(requestPath))
                ?? throw new InvalidOperationException("Некорректное задание печати");
            var pdfs = request.PdfPaths.Select(File.ReadAllBytes).ToList();
            PrintPdfs(
                pdfs,
                new PrintProfile(request.Printer, request.Settings),
                request.Copies);
            return 0;
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(Path.Combine(directory, "error.txt"), ex.Message); } catch { }
            return 1;
        }
    }

    public static void PrintPdfs(
        IEnumerable<byte[]> pdfs,
        PrintProfile profile,
        int copies = 1,
        bool rotatePortrait = false)
    {
        if (PeerPrint.IsRemote(profile.Printer))
        {
            var remote = new List<byte[]>();
            foreach (var pdf in pdfs)
            {
                if (pdf is { Length: > 0 })
                    remote.Add(pdf);
            }
            PeerPrint.Print(remote, profile, copies);
            return;
        }
        var options = PrintOptions.Parse(profile.Settings);
        var dpi = options.Paper == "a4" ? 200 : 300;
        var pages = new List<(Bitmap Bitmap, (double W, double H) Pts)>();
        try
        {
            foreach (var pdf in pdfs)
            {
                if (pdf is null || pdf.Length == 0)
                    continue;
                pages.AddRange(Rasterize(pdf, dpi, rotatePortrait));
            }
            if (pages.Count == 0)
                throw new InvalidOperationException("Пустой PDF");
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

    private static List<(Bitmap Bitmap, (double W, double H) Pts)> Rasterize(
        byte[] pdf,
        int dpi,
        bool rotatePortrait = false)
    {
        using var stream = new MemoryStream(pdf, writable: false);
        var pages = new List<(Bitmap, (double, double))>();
        foreach (var sk in Conversion.ToImages(stream, options: new RenderOptions { Dpi = dpi }))
        {
            using (sk)
            {
                var bmp = ToBitmap(sk);
                if (rotatePortrait && bmp.Height > bmp.Width)
                    bmp.RotateFlip(RotateFlipType.Rotate90FlipNone);
                pages.Add((bmp, (bmp.Width * 72.0 / dpi, bmp.Height * 72.0 / dpi)));
            }
        }
        if (pages.Count == 0)
            throw new InvalidOperationException("Empty PDF");
        return pages;
    }

    private static Bitmap ToBitmap(SKBitmap sk)
    {
        using var src = sk.ColorType == SKColorType.Bgra8888 ? null : sk.Copy(SKColorType.Bgra8888);
        var pixels = src ?? sk;
        var bmp = new Bitmap(pixels.Width, pixels.Height, PixelFormat.Format32bppPArgb);
        var data = bmp.LockBits(
            new Rectangle(0, 0, bmp.Width, bmp.Height),
            ImageLockMode.WriteOnly,
            PixelFormat.Format32bppPArgb);
        try
        {
            var srcPtr = pixels.GetPixels();
            var srcStride = pixels.RowBytes;
            var dstStride = data.Stride;
            var rowBytes = Math.Min(srcStride, dstStride);
            var row = new byte[rowBytes];
            for (var y = 0; y < pixels.Height; y++)
            {
                Marshal.Copy(IntPtr.Add(srcPtr, y * srcStride), row, 0, rowBytes);
                Marshal.Copy(row, 0, IntPtr.Add(data.Scan0, y * dstStride), rowBytes);
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        return bmp;
    }

    private static void PrintPages(
        List<(Bitmap Bitmap, (double W, double H) Pts)> pages,
        string printer,
        int copies,
        (bool Landscape, bool Noscale, string Paper, double? WidthMm, double? HeightMm) options)
    {
        using var doc = new PrintDocument();
        doc.DocumentName = "Warehouse packing";
        doc.PrintController = new StandardPrintController();
        doc.OriginAtMargins = false;
        doc.PrinterSettings.PrinterName = ResolvePrinter(printer);
        // Для одной этикетки передаём тираж драйверу одним заданием.
        // Повторная отрисовка одной страницы заставляет некоторые термопринтеры
        // печатать тираж небольшими порциями с паузами между ними.
        var driverCopies = pages.Count == 1 && doc.PrinterSettings.MaximumCopies >= copies
            ? copies
            : 1;
        doc.PrinterSettings.Copies = (short)driverCopies;
        doc.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
        ApplyPaper(doc, options);

        var pageIndex = 0;
        var copyIndex = 0;
        var renderedCopies = driverCopies > 1 ? 1 : copies;
        doc.PrintPage += (_, e) =>
        {
            var (image, pts) = pages[pageIndex];
            var area = e.PageBounds;
            if (e.Graphics is null)
                return;
            e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
            e.Graphics.SmoothingMode = SmoothingMode.None;
            e.Graphics.CompositingMode = CompositingMode.SourceCopy;
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
            e.HasMorePages = copyIndex < renderedCopies;
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

    private sealed class PrintWorkerRequest
    {
        public string Printer { get; set; } = "";
        public string Settings { get; set; } = "";
        public int Copies { get; set; } = 1;
        public List<string> PdfPaths { get; set; } = [];
    }
}
