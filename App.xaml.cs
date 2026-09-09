using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PDFtoImage;
using WarehousePacking.Models;
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
            RunWindowSmoke();
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

    /// XAML resource and template errors only surface when the window is
    /// actually built, so build one and snapshot each tab without hitting the
    /// network.
    private static void RunWindowSmoke()
    {
        using var client = new ApiClient("http://127.0.0.1:1");
        var main = new MainWindow(AppConfig.Load(), client, "smoke");
        FillDemoRows(main);
        var dir = Path.Combine(AppContext.BaseDirectory, "smoke");
        Directory.CreateDirectory(dir);
        foreach (var (index, name) in new[] { (0, "tasks"), (1, "fbs"), (2, "catalog") })
        {
            main.Tabs.SelectedIndex = index;
            Snapshot(main, Path.Combine(dir, $"tab_{name}.png"));
        }
        Console.WriteLine($"OK MainWindow built -> {dir}");
        main.Close();
    }

    private static void FillDemoRows(MainWindow main)
    {
        if (main.CatalogGrid.ItemsSource is ObservableCollection<CatalogRow> catalog)
        {
            catalog.Add(new CatalogRow { Id = 1, Sku = "SS288", Name = "Ошейник светоотражающий, размер M" });
            catalog.Add(new CatalogRow { Id = 2, Sku = "SS864", Name = "Поводок нейлоновый 2 м" });
            catalog.Add(new CatalogRow { Id = 3, Sku = "KIT-12", Name = "Набор для прогулок (комплект)" });
        }
        if (main.FbsLinesGrid.ItemsSource is ObservableCollection<FbsLineRow> lines)
        {
            lines.Add(new FbsLineRow { Id = 1, Seq = "1", Sku = "SS288", Name = "Ошейник светоотражающий", Order = "275254385", Status = "В сборке" });
            lines.Add(new FbsLineRow { Id = 2, Seq = "2", Sku = "SS864", Name = "Поводок нейлоновый 2 м", Order = "275254413", Status = "Готово" });
            lines.Add(new FbsLineRow { Id = 3, Seq = "3", Sku = "SS901", Name = "Шлейка регулируемая", Order = "275701560", Status = "Печать" });
        }
        if (main.FbsJobsGrid.ItemsSource is ObservableCollection<FbsJobRow> jobs)
        {
            jobs.Add(new FbsJobRow { Id = 49, Status = "В работе", Progress = "12/607" });
            jobs.Add(new FbsJobRow { Id = 46, Status = "Готово", Progress = "173/173" });
        }
        if (main.TasksGrid.ItemsSource is ObservableCollection<TaskRow> tasks)
        {
            tasks.Add(new TaskRow { Id = 1, Assembly = "09.09.2026", Marketplace = "Wildberries", Ship = "09.09.2026", Tag = "today" });
            tasks.Add(new TaskRow { Id = 2, Assembly = "09.09.2026", Marketplace = "Ozon", Ship = "10.09.2026", Tag = "tomorrow" });
        }
    }

    private static void Snapshot(Window window, string path)
    {
        const int width = 1400;
        const int height = 900;
        if (window.Content is not UIElement root)
            return;
        for (var pass = 0; pass < 3; pass++)
        {
            root.Measure(new Size(width, height));
            root.Arrange(new Rect(0, 0, width, height));
            root.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);
        }
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));
        using var file = File.Create(path);
        encoder.Save(file);
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
        foreach (var sk in Conversion.ToImages(stream, options: new PDFtoImage.RenderOptions { Dpi = 300 }))
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
