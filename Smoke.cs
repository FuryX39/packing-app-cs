using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PDFtoImage;
using WarehousePacking.Models;
using WarehousePacking.Services;
using WarehousePacking.Views;

namespace WarehousePacking;

/// Self-check for the things the compiler cannot see: XAML resources, cell
/// templates and whether a click on a templated button actually reaches its
/// handler. Runs against a throwaway loopback server, never a real one.
internal static class Smoke
{
    private static int _failures;

    public static int Run()
    {
        _failures = 0;
        var dir = Path.Combine(AppContext.BaseDirectory, "smoke");
        Directory.CreateDirectory(dir);
        CheckLabel(dir);
        CheckWindow(dir);
        Console.WriteLine(_failures == 0 ? "SMOKE OK" : $"SMOKE FAILED ({_failures})");
        return _failures;
    }

    private static void Pass(string message) => Console.WriteLine($"  ok   {message}");

    private static void Fail(string message)
    {
        _failures++;
        Console.WriteLine($"  FAIL {message}");
    }

    private static void CheckLabel(string dir)
    {
        var pdf = BarcodeLabel.LabelPdf("4600000000001", "TEST-SKU", "Тестовая этикетка", 47, 25);
        File.WriteAllBytes(Path.Combine(dir, "label.pdf"), pdf);
        using var stream = new MemoryStream(pdf, writable: false);
        var pages = 0;
        foreach (var page in Conversion.ToImages(stream, options: new PDFtoImage.RenderOptions { Dpi = 300 }))
        {
            using (page)
            using (var image = SkiaSharp.SKImage.FromBitmap(page))
            using (var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 92))
                File.WriteAllBytes(Path.Combine(dir, $"label_page{pages}.png"), data.ToArray());
            pages++;
        }
        if (pages == 1)
            Pass($"label pdf {pdf.Length} bytes rasterised to 1 page");
        else
            Fail($"label pdf rasterised to {pages} pages");
    }

    private static void CheckWindow(string dir)
    {
        using var server = new FakeServer();
        using var client = new ApiClient(server.Url, server.Url);
        try
        {
            // Off the dispatcher: blocking on it here would deadlock the
            // continuations that want to come back to this thread.
            Task.Run(() => client.LoginAsync("123", "x")).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Fail($"login against fake server: {ex.Message}");
            return;
        }
        if (!client.ApiOk)
        {
            Fail($"api session not established: {client.ApiError}");
            return;
        }
        Pass("logged in to fake server (web + api sessions)");

        var main = new MainWindow(AppConfig.Load(), client, "smoke");
        main.Width = 1400;
        main.Height = 900;
        main.WindowState = WindowState.Normal;
        main.Left = -4000;
        Application.Current.MainWindow = main;
        main.Show();
        Pump();

        foreach (var (index, name) in new[] { (0, "tasks"), (1, "fbs"), (2, "catalog") })
        {
            main.Tabs.SelectedIndex = index;
            Pump();
            Snapshot(main, Path.Combine(dir, $"tab_{name}.png"));
        }

        CheckFbsJobOpens(main);

        main.Tabs.SelectedIndex = 2;
        if (!WaitFor(() => main.CatalogGrid.Items.Count > 0))
        {
            Fail("catalog stayed empty");
            main.Close();
            return;
        }
        main.UpdateLayout();
        if (!WaitFor(() => PrintButton(main) is not null))
        {
            Fail($"catalog has {main.CatalogGrid.Items.Count} row(s) but no print button was realised");
            main.Close();
            return;
        }
        Pass($"catalog loaded {main.CatalogGrid.Items.Count} row(s) with print buttons");
        CheckRightClickSelectsRow(main);
        CheckPrintButtonReachesHandler(main);
        main.Close();
    }

    /// The context menu acts on the selected row, and a right click does not
    /// select one by itself.
    private static void CheckRightClickSelectsRow(MainWindow main)
    {
        main.CatalogGrid.SelectedItem = null;
        var cell = FindChildren<DataGridCell>(main.CatalogGrid).FirstOrDefault();
        if (cell is null)
        {
            Fail("no catalog cell in visual tree");
            return;
        }
        main.CatalogGrid.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
        {
            RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent,
            Source = cell,
        });
        if (main.CatalogGrid.SelectedItem is CatalogRow row)
            Pass($"right click selected row {row.Sku}");
        else
            Fail("right click did not select a row");
    }

    /// Clicks the templated print button and looks for the status it sets
    /// before awaiting, which proves the handler ran with the right row.
    private static void CheckPrintButtonReachesHandler(MainWindow main)
    {
        var button = PrintButton(main);
        if (button is null)
        {
            Fail("print button not found in catalog grid");
            return;
        }
        if (button.DataContext is not CatalogRow row)
        {
            Fail($"print button data context is {button.DataContext?.GetType().Name ?? "null"}, not a catalog row");
            return;
        }
        var before = main.StatusText.Text;
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
        var after = main.StatusText.Text;
        if (after != before && after.Contains(row.Sku))
            Pass($"print click handled for {row.Sku}: \"{after}\"");
        else if (after != before)
            Pass($"print click handled: \"{after}\"");
        else
            Fail($"print click changed nothing (status still \"{after}\")");
    }

    private static bool WaitFor(Func<bool> ready, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (ready())
                return true;
            Pump();
            Application.Current?.MainWindow?.UpdateLayout();
            Thread.Sleep(25);
        }
        return ready();
    }

    /// Runs before Application.Run, so the message loop has to be pumped by
    /// hand with a nested frame rather than by awaiting the dispatcher.
    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }

    /// Selecting a job must open exactly that job, and the row list must not be
    /// torn down and rebuilt while the selection is being handled.
    private static void CheckFbsJobOpens(MainWindow main)
    {
        main.Tabs.SelectedIndex = 1;
        if (!WaitFor(() => main.FbsJobsGrid.Items.Count > 0))
        {
            Fail("fbs job list stayed empty");
            return;
        }
        WaitFor(() => false, 400); // let the tab's own refresh settle first
        var rebuilds = 0;
        if (main.FbsJobsGrid.ItemsSource is ObservableCollection<FbsJobRow> jobs)
        {
            jobs.CollectionChanged += (_, args) =>
            {
                if (args.Action == NotifyCollectionChangedAction.Reset)
                    rebuilds++;
            };
        }
        main.FbsJobsGrid.SelectedIndex = 0;
        if (!WaitFor(() => main.FbsJobTitle.Text.Contains("49")))
        {
            Fail($"selecting job 49 did not open it (title \"{main.FbsJobTitle.Text}\")");
            return;
        }
        if (rebuilds > 0)
            Fail($"job list rebuilt {rebuilds} time(s) while opening a job");
        else
            Pass($"job 49 opened on selection, {main.FbsLinesGrid.Items.Count} line(s) shown");
    }

    private static Button? PrintButton(MainWindow main) =>
        FindChildren<Button>(main.CatalogGrid).FirstOrDefault(b => Equals(b.Content, "Печать ШК"));

    private static void Snapshot(Window window, string path)
    {
        if (window.Content is not UIElement root)
            return;
        var width = (int)Math.Max(320, root.RenderSize.Width);
        var height = (int)Math.Max(240, root.RenderSize.Height);
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private static IEnumerable<T> FindChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed)
                yield return typed;
            foreach (var nested in FindChildren<T>(child))
                yield return nested;
        }
    }

    /// Minimal HTTP/1.1 loopback server. Raw sockets avoid the URL ACL that
    /// HttpListener needs for a non-elevated prefix.
    private sealed class FakeServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();

        public string Url { get; }

        public FakeServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Url = $"http://127.0.0.1:{port}";
            // On the pool, not the dispatcher: the caller blocks the UI thread
            // while waiting for responses.
            _ = Task.Run(AcceptLoopAsync);
        }

        private async Task AcceptLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient socket;
                try { socket = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch { return; }
                _ = HandleAsync(socket);
            }
        }

        private static async Task HandleAsync(TcpClient socket)
        {
            using (socket)
            {
                try
                {
                    using var stream = socket.GetStream();
                    var head = new StringBuilder();
                    var one = new byte[1];
                    while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                    {
                        if (await stream.ReadAsync(one) == 0)
                            return;
                        head.Append((char)one[0]);
                    }
                    var lines = head.ToString().Split("\r\n");
                    var target = lines[0].Split(' ') is { Length: >= 2 } parts ? parts[1] : "/";
                    var length = lines
                        .Select(l => l.Split(':', 2))
                        .Where(p => p.Length == 2 && p[0].Trim().Equals("content-length", StringComparison.OrdinalIgnoreCase))
                        .Select(p => int.TryParse(p[1].Trim(), out var n) ? n : 0)
                        .FirstOrDefault();
                    var body = new byte[length];
                    var read = 0;
                    while (read < length)
                    {
                        var got = await stream.ReadAsync(body.AsMemory(read));
                        if (got == 0) break;
                        read += got;
                    }
                    var payload = Encoding.UTF8.GetBytes(Respond(target));
                    var header = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\n" +
                        "Content-Type: application/json; charset=utf-8\r\n" +
                        $"Content-Length: {payload.Length}\r\n" +
                        "Connection: close\r\n\r\n");
                    await stream.WriteAsync(header);
                    await stream.WriteAsync(payload);
                    await stream.FlushAsync();
                }
                catch { }
            }
        }

        private static string Respond(string target)
        {
            var path = target.Split('?')[0];
            if (path.StartsWith("/api/warehouse/catalog/products/", StringComparison.Ordinal))
                return """{"product":{"id":1,"sku":"SS288","name":"Ошейник","barcodes":[{"barcode":"4600000000001","label":"","group":""}]}}""";
            return path switch
            {
                "/api/warehouse/session" => """{"user":{"id":10,"login":"123","name":"Смоук"}}""",
                "/api/warehouse/tasks/my" => """{"tasks":[]}""",
                "/api/warehouse/tasks/statuses" => """{"task_statuses":[{"id":1,"name":"Новый"},{"id":2,"name":"В работе"}]}""",
                "/api/warehouse/catalog/products" =>
                    """{"products":[{"id":1,"sku":"SS288","name":"Ошейник светоотражающий","image_url":""},{"id":2,"sku":"SS864","name":"Поводок нейлоновый","image_url":""}]}""",
                "/api/v1/fbs-packing/my" => """{"jobs":[{"id":49,"status":"in_progress","line_done":1,"line_total":3}]}""",
                "/api/v1/fbs-packing/jobs/49/pack" =>
                    """
                    {"job":{"id":49,"status":"in_progress","line_done":1,"line_total":2,"line_pending":1,
                    "lines":[{"id":901,"seq":"1","sku":"SS288","product_name":"Ошейник","order_id":"275254385","status":"pending","image_url":""},
                    {"id":902,"seq":"2","sku":"SS864","product_name":"Поводок","order_id":"275254413","status":"done","image_url":""}],
                    "remaining_groups":[{"sku":"SS288","name":"Ошейник","quantity":1,"barcode":"4600000000001","image_url":""}]}}
                    """,
                _ => "{}",
            };
        }

        public void Dispose()
        {
            _stop.Cancel();
            try { _listener.Stop(); } catch { }
            _stop.Dispose();
        }
    }
}
