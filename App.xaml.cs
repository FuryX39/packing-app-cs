using System.IO;
using System.Windows;
using PDFtoImage;
using WarehousePacking.Services;
using WarehousePacking.Views;

namespace WarehousePacking;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Any(a => string.Equals(a, "--smoke-label", StringComparison.OrdinalIgnoreCase)))
        {
            RunLabelSmoke();
            Shutdown();
            return;
        }

        base.OnStartup(e);
        var login = new LoginWindow();
        if (login.ShowDialog() == true && login.Client is { } client)
        {
            var main = new MainWindow(login.Config, client, login.UserName);
            MainWindow = main;
            main.Show();
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            return;
        }
        Shutdown();
    }

    private static void RunLabelSmoke()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "smoke");
        Directory.CreateDirectory(dir);
        var pdf = BarcodeLabel.LabelPdf("4600000000001", "TEST-SKU", "Тестовая этикетка", 47, 25);
        var pdfPath = Path.Combine(dir, "label.pdf");
        File.WriteAllBytes(pdfPath, pdf);
        using var stream = new MemoryStream(pdf, writable: false);
        var index = 0;
        foreach (var sk in Conversion.ToImages(stream, options: new RenderOptions { Dpi = 300 }))
        {
            using (sk)
            using (var image = SkiaSharp.SKImage.FromBitmap(sk))
            using (var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 92))
            {
                File.WriteAllBytes(Path.Combine(dir, $"page{index}.png"), data.ToArray());
            }
            index++;
        }
        if (index == 0)
            throw new InvalidOperationException("PDF raster produced no pages");
        Console.WriteLine($"OK {pdf.Length} bytes, {index} page(s) -> {dir}");
    }
}
