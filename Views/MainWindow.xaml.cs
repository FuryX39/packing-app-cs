using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WarehousePacking.Models;
using WarehousePacking.Services;

namespace WarehousePacking.Views;

public partial class MainWindow : Window
{
    private AppConfig _config;
    private readonly ApiClient _client;
    private readonly DispatcherTimer _tasksTimer = new();
    private bool _printBusy;
    private bool _fbsBusy;
    private bool _taskStatusSilent;
    private bool _fbsJobsFilling;
    private bool _catalogLoading;

    private List<JsonMap> _tasks = [];
    private JsonMap? _currentTask;
    private List<JsonMap> _taskStatuses = [];
    private readonly Dictionary<string, int> _statusIdByName = [];

    private List<JsonMap> _catalogAll = [];
    private List<JsonMap> _catalogFiltered = [];
    private int _catalogPage;
    private readonly Dictionary<int, List<Dictionary<string, string>>> _barcodeCache = [];
    private readonly Dictionary<string, string> _catalogImageUrls = [];
    private DispatcherTimer? _catalogSearchTimer;

    private List<JsonMap> _fbsJobs = [];
    private JsonMap? _fbsJob;
    private int _fbsJobsPage;
    private int _fbsLinesPage;
    private object? _fbsPagedJobId;
    private JsonMap? _selectedGroup;
    private string _lastScanCode = "";
    private string _lastScanSku = "";
    private List<int> _lastScanLineIds = [];
    private int? _prefetchJobId;
    private DispatcherTimer? _fbsSearchTimer;
    private readonly Dictionary<string, ImageSource> _photoCache = [];

    public MainWindow(AppConfig config, ApiClient client, string userName)
    {
        InitializeComponent();
        _config = config;
        _client = client;
        Title = $"Warehouse Packing — {userName}";
        WindowState = WindowState.Maximized;
        FbsSkipMpBox.IsChecked = config.SkipMpConfirm;
        _tasksTimer.Tick += (_, _) =>
        {
            if (Tabs.SelectedIndex == 0)
                _ = LoadTasksAsync(true);
        };
        Loaded += async (_, _) => await LoadTasksAsync(false);
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private async void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _tasksTimer.Stop();
        await _client.LogoutAsync();
        _client.Dispose();
    }

    private void OnLogout(object sender, RoutedEventArgs e) => Close();

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow(_config, _client) { Owner = this, CacheHintChanged = UpdateFbsCacheHint };
        if (dlg.ShowDialog() == true)
        {
            _config = AppConfig.Load();
            FbsSkipMpBox.IsChecked = _config.SkipMpConfirm;
            SetStatus("Настройки сохранены");
            RestartTasksTimer();
        }
    }

    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        switch (Tabs.SelectedIndex)
        {
            case 0:
                _ = LoadTasksAsync(false);
                break;
            case 1:
                _tasksTimer.Stop();
                _ = LoadFbsJobsAsync();
                break;
            case 2:
                _tasksTimer.Stop();
                _ = LoadCatalogAsync(false);
                break;
        }
    }

    private void RestartTasksTimer()
    {
        _tasksTimer.Stop();
        _tasksTimer.Interval = TimeSpan.FromMilliseconds(_config.RefreshMs);
        if (Tabs.SelectedIndex == 0)
            _tasksTimer.Start();
    }

    private void ShowError(Exception ex)
    {
        if (ex is AuthException)
        {
            MessageBox.Show(this, ex.Message, "Сессия", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
            return;
        }
        if (ex is InvalidOperationException or ArgumentException)
        {
            MessageBox.Show(this, ex.Message, "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
            SetStatus("Ошибка");
            return;
        }
        MessageBox.Show(this, ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        SetStatus("Ошибка");
    }

    private async Task RunPrint(string status, Func<Task> work)
    {
        if (_printBusy) return;
        _printBusy = true;
        SetStatus(status);
        try { await Task.Run(work); }
        catch (Exception ex) { ShowError(ex); }
        finally { _printBusy = false; }
    }

    private ImageSource? Thumb(string url, int size)
    {
        url = (url ?? "").Trim();
        if (url.Length == 0) return null;
        var key = $"{size}:{url}";
        if (_photoCache.TryGetValue(key, out var cached))
            return cached;
        var data = ImageCache.GetCached(url);
        if (data is null)
        {
            _ = EnsureImageAsync(url);
            return null;
        }
        try
        {
            var img = BytesToImage(data, size);
            _photoCache[key] = img;
            return img;
        }
        catch { return null; }
    }

    private async Task EnsureImageAsync(string url)
    {
        if (!ImageCache.BeginFetch(url)) return;
        var data = await ImageCache.FetchAsync(url);
        if (data is null) return;
        await Dispatcher.InvokeAsync(RefreshLoadedImages);
    }

    private void RefreshLoadedImages()
    {
        RenderCatalogPage();
        RenderFbsLines();
        if (FbsManualBox.IsChecked == true)
            RenderRemaining();
        RefreshActiveImage();
    }

    private static BitmapImage BytesToImage(byte[] data, int decode = 0)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = new MemoryStream(data);
        if (decode > 0)
            bmp.DecodePixelWidth = decode;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    // --- Tasks ---

    private async void OnReloadTasks(object sender, RoutedEventArgs e) => await LoadTasksAsync(false);

    private async Task LoadTasksAsync(bool silent)
    {
        if (!silent) SetStatus("Загрузка заданий...");
        await EnsureStatusesAsync();
        try
        {
            _tasks = await _client.GetMyTasksAsync();
        }
        catch (Exception ex)
        {
            ShowError(ex);
            return;
        }
        var selected = _currentTask?.IntOrNull("id");
        TasksGrid.ItemsSource = _tasks.Select(t => new TaskRow
        {
            Id = t.Int("id"),
            Assembly = Paging.FormatDay(t.Str("start_date")),
            Marketplace = t.Str("counterparty_name", "—"),
            Ship = Paging.FormatDay(t.Str("end_date")),
            Tag = Paging.ShipTag(t.Str("end_date")),
        }).ToList();
        if (selected is int sid)
        {
            var row = ((IEnumerable<TaskRow>)TasksGrid.ItemsSource).FirstOrDefault(x => x.Id == sid);
            if (row != null)
            {
                TasksGrid.SelectedItem = row;
                await OpenTaskAsync(sid);
            }
            else
            {
                _currentTask = null;
                RenderTask();
            }
        }
        SetStatus($"Заданий: {_tasks.Count}");
        RestartTasksTimer();
    }

    private async Task EnsureStatusesAsync()
    {
        if (_taskStatuses.Count > 0) return;
        try { _taskStatuses = await _client.GetTaskStatusesAsync(); }
        catch { _taskStatuses = []; }
        _statusIdByName.Clear();
        foreach (var item in _taskStatuses)
        {
            var name = item.Str("name");
            var id = item.IntOrNull("id");
            if (name.Length > 0 && id is int n)
                _statusIdByName[name] = n;
        }
    }

    private async void OnTaskSelected(object sender, SelectionChangedEventArgs e)
    {
        if (TasksGrid.SelectedItem is not TaskRow row) return;
        await OpenTaskAsync(row.Id);
    }

    private async Task OpenTaskAsync(int taskId)
    {
        try
        {
            SetStatus("Загрузка задания...");
            _currentTask = await _client.GetTaskAsync(taskId);
            RenderTask();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void RenderTask()
    {
        var task = _currentTask;
        RenderTaskStatus(task);
        TaskDescription.Text = task is null ? "" : (task.Str("description").Trim().Length > 0 ? task.Str("description") : "—");
        var files = new ObservableCollection<AttachmentRow>();
        if (task is not null)
        {
            foreach (var att in task.Arr("attachments"))
            {
                var kind = att.Str("kind");
                files.Add(new AttachmentRow
                {
                    Id = att.Int("id"),
                    Kind = kind == "a4" ? "А4" : kind == "label" ? "Этикетки" : kind,
                    Filename = att.Str("filename", "file.pdf"),
                });
            }
        }
        FilesGrid.ItemsSource = files;
        var title = task?.Str("counterparty_name");
        if (string.IsNullOrWhiteSpace(title))
            title = task is null ? "" : $"Задание #{task.Str("id")}";
        if (title.Length > 0)
            SetStatus($"Открыто: {title}");
    }

    private void RenderTaskStatus(JsonMap? task)
    {
        _taskStatusSilent = true;
        TaskStatusBox.ItemsSource = _taskStatuses.Select(x => x.Str("name")).Where(x => x.Length > 0).ToList();
        if (task is null)
            TaskStatusBox.SelectedItem = null;
        else
        {
            var current = task.Str("status_name");
            var names = (List<string>)TaskStatusBox.ItemsSource;
            TaskStatusBox.SelectedItem = names.Contains(current) ? current : names.FirstOrDefault();
        }
        _taskStatusSilent = false;
    }

    private async void OnTaskStatusChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_taskStatusSilent || _currentTask is null) return;
        var name = TaskStatusBox.SelectedItem as string ?? "";
        if (!_statusIdByName.TryGetValue(name, out var statusId)) return;
        if (_currentTask.Int("status_id") == statusId) return;
        var taskId = _currentTask.Int("id");
        SetStatus("Сохранение статуса...");
        try
        {
            _currentTask = await _client.PatchTaskAsync(taskId, new { status_id = statusId });
            SyncTaskCache(_currentTask);
            await LoadTasksAsync(true);
            RenderTask();
            SetStatus($"Статус: {_currentTask.Str("status_name", name)}");
        }
        catch (Exception ex)
        {
            RenderTaskStatus(_currentTask);
            ShowError(ex);
        }
    }

    private void SyncTaskCache(JsonMap task)
    {
        var id = task.Int("id");
        for (var i = 0; i < _tasks.Count; i++)
        {
            if (_tasks[i].Int("id") == id)
            {
                _tasks[i] = task;
                return;
            }
        }
        _tasks.Add(task);
    }

    private async Task MaybeMarkInProgressAsync()
    {
        if (_currentTask is null || _currentTask.Str("status_name") != "Новый") return;
        await EnsureStatusesAsync();
        if (!_statusIdByName.TryGetValue("В работе", out var statusId)) return;
        try
        {
            _currentTask = await _client.PatchTaskAsync(_currentTask.Int("id"), new { status_id = statusId });
            SyncTaskCache(_currentTask);
            RenderTaskStatus(_currentTask);
            SetStatus($"Статус изменён: {_currentTask.Str("status_name", "В работе")}");
        }
        catch (AuthException ex) { ShowError(ex); }
        catch { SetStatus("Не удалось обновить статус задачи"); }
    }

    private List<JsonMap> TaskAttachments(string? kind)
    {
        var all = _currentTask?.Arr("attachments") ?? [];
        return kind is null ? all : all.Where(x => x.Str("kind") == kind).ToList();
    }

    private async void OnFilePrintClick(object sender, MouseButtonEventArgs e)
    {
        if (FilesGrid.CurrentColumn is not { DisplayIndex: 2 }) return;
        if (FilesGrid.SelectedItem is not AttachmentRow row) return;
        await PrintAttachmentAsync(row.Id);
    }

    private async Task PrintAttachmentAsync(int attachmentId)
    {
        if (_currentTask is null)
        {
            MessageBox.Show(this, "Выберите задание", "Нет задания");
            return;
        }
        var att = TaskAttachments(null).FirstOrDefault(x => x.Int("id") == attachmentId);
        if (att is null)
        {
            MessageBox.Show(this, "Файл не найден", "Нет файла");
            return;
        }
        var profile = att.Str("kind") == "a4" ? _config.A4Profile() : _config.LabelProfile();
        var taskId = _currentTask.Int("id");
        await RunPrint("Печать файла...", async () =>
        {
            var pdf = await _client.DownloadTaskAttachmentAsync(taskId, attachmentId);
            GdiPrinter.PrintPdf(pdf, profile);
        });
        if (!_printBusy)
        {
            SetStatus($"Файл «{att.Str("filename", "file.pdf")}» отправлен на печать");
            await MaybeMarkInProgressAsync();
        }
    }

    private async void OnPrintAllA4(object sender, RoutedEventArgs e) => await PrintKindAsync("a4");
    private async void OnPrintAllLabels(object sender, RoutedEventArgs e) => await PrintKindAsync("label");

    private async Task PrintKindAsync(string kind)
    {
        if (_currentTask is null)
        {
            MessageBox.Show(this, "Выберите задание", "Нет задания");
            return;
        }
        var items = TaskAttachments(kind);
        if (items.Count == 0)
        {
            MessageBox.Show(this, $"У задания нет PDF для печати ({(kind == "a4" ? "А4" : "этикетки")})", "Нет файлов");
            return;
        }
        var profile = kind == "a4" ? _config.A4Profile() : _config.LabelProfile();
        var taskId = _currentTask.Int("id");
        await RunPrint($"Печать {(kind == "a4" ? "А4" : "этикетки")}...", async () =>
        {
            foreach (var att in items)
                GdiPrinter.PrintPdf(await _client.DownloadTaskAttachmentAsync(taskId, att.Int("id")), profile);
        });
        SetStatus($"Напечатано файлов ({(kind == "a4" ? "А4" : "этикетки")}): {items.Count}");
        await MaybeMarkInProgressAsync();
    }

    private async void OnPrintAllFiles(object sender, RoutedEventArgs e)
    {
        if (_currentTask is null)
        {
            MessageBox.Show(this, "Выберите задание", "Нет задания");
            return;
        }
        var a4 = TaskAttachments("a4");
        var labels = TaskAttachments("label");
        if (a4.Count == 0 && labels.Count == 0)
        {
            MessageBox.Show(this, "У задания нет PDF для печати", "Нет файлов");
            return;
        }
        var taskId = _currentTask.Int("id");
        var a4p = _config.A4Profile();
        var lp = _config.LabelProfile();
        await RunPrint("Печать всех файлов...", async () =>
        {
            foreach (var att in a4)
                GdiPrinter.PrintPdf(await _client.DownloadTaskAttachmentAsync(taskId, att.Int("id")), a4p);
            foreach (var att in labels)
                GdiPrinter.PrintPdf(await _client.DownloadTaskAttachmentAsync(taskId, att.Int("id")), lp);
        });
        SetStatus($"Напечатано: А4 — {a4.Count}, этикетки — {labels.Count}");
        await MaybeMarkInProgressAsync();
    }

    // --- Catalog ---

    private void OnCatalogSearch(object sender, RoutedEventArgs e) => _ = LoadCatalogAsync(false);
    private void OnCatalogReload(object sender, RoutedEventArgs e) => _ = LoadCatalogAsync(true);

    private void OnCatalogSearchKey(object sender, KeyEventArgs e)
    {
        _catalogSearchTimer?.Stop();
        _catalogSearchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _catalogSearchTimer.Tick += (_, _) =>
        {
            _catalogSearchTimer.Stop();
            _ = LoadCatalogAsync(false);
        };
        _catalogSearchTimer.Start();
    }

    private async Task LoadCatalogAsync(bool force)
    {
        _catalogSearchTimer?.Stop();
        var query = CatalogSearchBox.Text.Trim().ToLowerInvariant();
        if (!force && _catalogAll.Count > 0)
        {
            ApplyCatalogFilter(query);
            return;
        }
        if (_catalogLoading) return;
        _catalogLoading = true;
        SetStatus("Загрузка номенклатуры...");
        try
        {
            _catalogAll = await _client.SearchCatalogProductsAsync();
            if (force) _barcodeCache.Clear();
            ApplyCatalogFilter(query);
        }
        catch (Exception ex) { ShowError(ex); }
        finally { _catalogLoading = false; }
    }

    private void ApplyCatalogFilter(string query)
    {
        _catalogFiltered = query.Length == 0
            ? [.. _catalogAll]
            : _catalogAll.Where(p => Paging.CatalogMatches(p, query)).ToList();
        _catalogPage = 0;
        RenderCatalogPage();
    }

    private void RenderCatalogPage()
    {
        var (visible, page) = Paging.Slice(_catalogFiltered, _catalogPage);
        _catalogPage = page;
        _catalogImageUrls.Clear();
        var rows = new List<CatalogRow>();
        foreach (var p in visible)
        {
            var id = p.Int("id");
            if (id == 0) continue;
            var name = p.Str("name");
            if (p.Flag("is_kit")) name += " (комплект)";
            var url = p.Str("image_url").Trim();
            _catalogImageUrls[id.ToString()] = url;
            rows.Add(new CatalogRow { Id = id, Sku = p.Str("sku"), Name = name, Photo = url.Length > 0 ? Thumb(url, 32) : null });
        }
        CatalogGrid.ItemsSource = rows;
        CatalogPageLabel.Text = Paging.RangeLabel(_catalogFiltered.Count, _catalogPage);
        CatalogPrev.IsEnabled = _catalogPage > 0;
        CatalogNext.IsEnabled = _catalogPage + 1 < Paging.PageCount(_catalogFiltered.Count);
        var q = CatalogSearchBox.Text.Trim();
        var prefix = q.Length > 0 ? $"Найдено: {_catalogFiltered.Count} из {_catalogAll.Count}" : $"Товаров: {_catalogFiltered.Count}";
        if (_catalogFiltered.Count > 0)
        {
            var start = _catalogPage * Paging.PageSize + 1;
            var end = Math.Min(_catalogFiltered.Count, (_catalogPage + 1) * Paging.PageSize);
            SetStatus($"{prefix} · {start}–{end}");
        }
        else SetStatus(prefix);
    }

    private void OnCatalogPrev(object sender, RoutedEventArgs e) { if (_catalogPage > 0) { _catalogPage--; RenderCatalogPage(); } }
    private void OnCatalogNext(object sender, RoutedEventArgs e)
    {
        if (_catalogPage + 1 < Paging.PageCount(_catalogFiltered.Count)) { _catalogPage++; RenderCatalogPage(); }
    }

    private async void OnCatalogPrintClick(object sender, MouseButtonEventArgs e)
    {
        if (CatalogGrid.CurrentColumn is not { DisplayIndex: 3 }) return;
        if (CatalogGrid.SelectedItem is not CatalogRow row) return;
        await PrintCatalogBarcodeAsync(row.Id);
    }

    private void OnCatalogContext(object sender, MouseButtonEventArgs e)
    {
        if (CatalogGrid.SelectedItem is not CatalogRow row) return;
        var menu = new ContextMenu();
        var item = new MenuItem { Header = "Добавить ШК" };
        item.Click += (_, _) => _ = AddCatalogBarcodeAsync(row.Id);
        menu.Items.Add(item);
        menu.IsOpen = true;
    }

    private JsonMap? CatalogById(int id) =>
        _catalogFiltered.FirstOrDefault(p => p.Int("id") == id) ?? _catalogAll.FirstOrDefault(p => p.Int("id") == id);

    private async Task PrintCatalogBarcodeAsync(int productId)
    {
        var product = CatalogById(productId);
        if (product is null)
        {
            MessageBox.Show(this, "Товар не найден — обновите список", "Нет товара");
            return;
        }
        if (!_barcodeCache.TryGetValue(productId, out var barcodes))
        {
            SetStatus("Загрузка штрихкодов...");
            try
            {
                var details = await _client.GetCatalogProductAsync(productId);
                barcodes = Paging.NormalizeBarcodes(details);
                if (barcodes.Count == 0)
                {
                    var sku = product.Str("sku").Trim();
                    if (sku.Length == 0) throw new InvalidOperationException("У товара нет штрихкода и артикула");
                    barcodes = [new() { ["barcode"] = sku, ["label"] = "", ["group"] = "" }];
                }
                _barcodeCache[productId] = barcodes;
            }
            catch (Exception ex) { ShowError(ex); return; }
        }
        Dictionary<string, string>? chosen = barcodes[0];
        if (barcodes.Count > 1)
        {
            var menu = new ContextMenu();
            Dictionary<string, string>? pick = null;
            foreach (var bc in barcodes)
            {
                var mi = new MenuItem { Header = Paging.BarcodeComboLabel(bc) };
                mi.Click += (_, _) => pick = bc;
                menu.Items.Add(mi);
            }
            menu.IsOpen = true;
            await Task.Delay(50);
            // fallback: if user didn't click, show first via dialog-less pick of first after menu — use simple choice window
            var choice = PickBarcode(barcodes);
            if (choice is null) return;
            chosen = choice;
        }
        await PrintBarcodeItemAsync(product, chosen);
    }

    private Dictionary<string, string>? PickBarcode(List<Dictionary<string, string>> barcodes)
    {
        if (barcodes.Count == 1) return barcodes[0];
        var w = new Window
        {
            Title = "Штрихкод",
            Width = 360,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
        };
        var panel = new StackPanel { Margin = new Thickness(12) };
        Dictionary<string, string>? result = null;
        foreach (var bc in barcodes)
        {
            var b = new System.Windows.Controls.Button { Content = Paging.BarcodeComboLabel(bc), Margin = new Thickness(0, 0, 0, 6), HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch };
            var captured = bc;
            b.Click += (_, _) => { result = captured; w.DialogResult = true; w.Close(); };
            panel.Children.Add(b);
        }
        w.Content = panel;
        return w.ShowDialog() == true ? result : null;
    }

    private int CatalogCopies()
    {
        if (!int.TryParse(CatalogQtyBox.Text.Trim(), out var n)) n = 1;
        return Math.Clamp(n, 1, 9999);
    }

    private async Task PrintBarcodeItemAsync(JsonMap product, Dictionary<string, string> item)
    {
        var barcode = item.GetValueOrDefault("barcode")?.Trim() ?? "";
        var sku = product.Str("sku", barcode);
        var name = product.Str("name");
        var copies = CatalogCopies();
        var size = PrintOptions.LabelSizeMm(_config.LabelSettings);
        var profile = _config.LabelProfile();
        await RunPrint("Печать штрихкода...", () =>
        {
            var pdf = BarcodeLabel.LabelPdf(barcode, sku, name, size.WidthMm, size.HeightMm);
            GdiPrinter.PrintPdf(pdf, profile, copies);
            return Task.CompletedTask;
        });
        SetStatus($"ШК {barcode}: напечатано {copies} шт.");
    }

    private async Task AddCatalogBarcodeAsync(int productId)
    {
        var product = CatalogById(productId);
        if (product is null)
        {
            MessageBox.Show(this, "Товар не найден — обновите список", "Нет товара");
            return;
        }
        var dlg = new AddBarcodeWindow(product.Str("sku"), product.Str("name")) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        SetStatus("Сохранение штрихкода...");
        try
        {
            var payload = await _client.AddCatalogBarcodeAsync(productId, dlg.BarcodeText, dlg.LabelText, dlg.GroupText);
            var barcodes = Paging.NormalizeBarcodes(payload.Obj("product"));
            if (barcodes.All(x => x.GetValueOrDefault("barcode") != dlg.BarcodeText))
                barcodes.Add(new() { ["barcode"] = dlg.BarcodeText, ["label"] = dlg.LabelText, ["group"] = dlg.GroupText });
            _barcodeCache[productId] = barcodes;
            SetStatus(payload.Str("result") == "updated"
                ? $"ШК {dlg.BarcodeText} уже был у {product.Str("sku")}"
                : $"ШК {dlg.BarcodeText} добавлен к {product.Str("sku")}");
        }
        catch (ApiException ex)
        {
            MessageBox.Show(this, ex.Message, "Штрихкод", MessageBoxButton.OK, MessageBoxImage.Error);
            SetStatus(ex.Message);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    // --- FBS ---

    private bool RequireApi()
    {
        if (_client.ApiOk)
        {
            FbsApiHint.Text = $"API: {_client.ApiUrl}";
            FbsApiHint.Foreground = Brushes.DarkGreen;
            return true;
        }
        FbsApiHint.Text = string.IsNullOrWhiteSpace(_client.ApiError) ? "Нет сессии API — нужен run_api.py" : _client.ApiError;
        FbsApiHint.Foreground = Brushes.Firebrick;
        return false;
    }

    private void FocusScan() => FbsScanBox.Focus();

    private async void OnReloadFbs(object sender, RoutedEventArgs e) => await LoadFbsJobsAsync();

    private async Task LoadFbsJobsAsync()
    {
        if (!RequireApi())
        {
            MessageBox.Show(this, string.IsNullOrWhiteSpace(_client.ApiError)
                ? "Запустите python run_api.py (порт 8766) и укажите адрес API в настройках."
                : _client.ApiError, "API упаковщиков");
            return;
        }
        SetStatus("Загрузка FBS...");
        try
        {
            _fbsJobs = await _client.FbsMyJobsAsync();
            var selected = _fbsJob?.IntOrNull("id");
            if (selected is int sid)
            {
                var idx = _fbsJobs.FindIndex(j => j.Int("id") == sid);
                if (idx >= 0) _fbsJobsPage = idx / Paging.PageSize;
            }
            RenderFbsJobs();
            SetStatus($"FBS заданий: {_fbsJobs.Count}");
            FocusScan();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void RenderFbsJobs()
    {
        var (visible, page) = Paging.Slice(_fbsJobs, _fbsJobsPage);
        _fbsJobsPage = page;
        _fbsJobsFilling = true;
        FbsJobsGrid.ItemsSource = visible.Select(j => new FbsJobRow
        {
            Id = j.Int("id"),
            Status = Paging.JobStatusRu(j.Str("status")),
            Progress = $"{j.Int("line_done")}/{j.Int("line_total")}",
        }).ToList();
        var selected = _fbsJob?.IntOrNull("id");
        if (selected is int sid)
            FbsJobsGrid.SelectedItem = ((IEnumerable<FbsJobRow>)FbsJobsGrid.ItemsSource).FirstOrDefault(x => x.Id == sid);
        _fbsJobsFilling = false;
        FbsJobsPageLabel.Text = Paging.RangeLabel(_fbsJobs.Count, _fbsJobsPage);
        FbsJobsPrev.IsEnabled = _fbsJobsPage > 0;
        FbsJobsNext.IsEnabled = _fbsJobsPage + 1 < Paging.PageCount(_fbsJobs.Count);
    }

    private void OnFbsJobsPrev(object sender, RoutedEventArgs e) { if (_fbsJobsPage > 0) { _fbsJobsPage--; RenderFbsJobs(); } }
    private void OnFbsJobsNext(object sender, RoutedEventArgs e)
    {
        if (_fbsJobsPage + 1 < Paging.PageCount(_fbsJobs.Count)) { _fbsJobsPage++; RenderFbsJobs(); }
    }

    private async void OnFbsJobSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_fbsJobsFilling || FbsJobsGrid.SelectedItem is not FbsJobRow row) return;
        SetStatus("Открытие FBS...");
        try
        {
            _fbsJob = await _client.FbsOpenJobAsync(row.Id);
            RenderFbsJob();
            SetStatus($"FBS задание #{_fbsJob.Str("id")}");
            FocusScan();
            _ = PrefetchLabelsAsync(_fbsJob, false);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void RenderFbsJob()
    {
        var job = _fbsJob;
        FbsJobTitle.Text = job is null ? "Выберите задание" : $"Задание #{job.Str("id")}";
        var done = job?.Int("line_done") ?? 0;
        var total = job?.Int("line_total") ?? 0;
        var pending = job?.Int("line_pending", job?.Int("remaining") ?? 0) ?? 0;
        var printed = job?.Int("line_printed") ?? 0;
        FbsJobStats.Text = $"готово {done}/{total} · осталось {pending} · в печати {printed}";
        UpdateFbsCacheHint();
        FbsCisHint.Text = job?.Flag("require_cis") == true
            ? "Обязательная маркировка ЧЗ: для товаров с GTIN пикайте КИЗ (Data Matrix), не обычный ШК."
            : "";
        var actives = ActiveLines();
        var skip = FbsSkipMpBox.IsChecked == true;
        if (actives.Count > 0)
        {
            FbsActiveText.Text = Paging.FormatPicked(actives, skip);
            SetActiveImage(actives[0].Str("image_url"));
        }
        else
        {
            var picked = LastPickedLines();
            if (skip && picked.Count > 0)
            {
                FbsActiveText.Text = Paging.FormatPicked(picked, true);
                SetActiveImage(picked[0].Str("image_url"));
            }
            else
            {
                var hint = job?.Flag("require_cis") == true ? "пикните КИЗ или товар" : "пикните товар";
                if (skip) hint += " (без подтверждения ШК МП)";
                FbsActiveText.Text = $"Нет активной строки — {hint}";
                SetActiveImage("");
            }
        }
        var jid = job?.IntOrNull("id");
        if (!Equals(_fbsPagedJobId, jid))
        {
            _fbsLinesPage = 0;
            _fbsPagedJobId = jid;
            FbsLinesSearch.Text = "";
        }
        if (actives.Count > 0 && FbsLinesSearch.Text.Trim().Length == 0)
        {
            var aid = actives[0].Str("id");
            var idx = (job?.Arr("lines") ?? []).FindIndex(l => l.Str("id") == aid);
            if (idx >= 0) _fbsLinesPage = idx / Paging.PageSize;
        }
        RenderFbsLines();
        if (FbsManualBox.IsChecked == true)
            RenderRemaining();
    }

    private void SetActiveImage(string url) => FbsActiveImage.Source = string.IsNullOrWhiteSpace(url) ? null : Thumb(url, 110);

    private void RefreshActiveImage()
    {
        var actives = ActiveLines();
        if (actives.Count > 0) { SetActiveImage(actives[0].Str("image_url")); return; }
        var last = LastPickedLines();
        if (FbsSkipMpBox.IsChecked == true && last.Count > 0)
            SetActiveImage(last[0].Str("image_url"));
        else
            SetActiveImage("");
    }

    private List<JsonMap> ActiveLines()
    {
        var job = _fbsJob;
        if (job is null) return [];
        var lines = job.Arr("active_lines");
        if (lines.Count > 0) return lines;
        var one = job.Obj("active_line");
        return one is null ? [] : [one];
    }

    private List<JsonMap> LastPickedLines()
    {
        var ids = _lastScanLineIds.ToHashSet();
        if (ids.Count == 0 || _fbsJob is null) return [];
        return _fbsJob.Arr("lines").Where(l => l.IntOrNull("id") is int id && ids.Contains(id)).ToList();
    }

    private List<JsonMap> FilteredLines()
    {
        var job = _fbsJob;
        if (job is null) return [];
        var query = FbsLinesSearch.Text.Trim();
        var barcodes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in job.Arr("remaining_groups"))
        {
            var sku = g.Str("sku").Trim().ToLowerInvariant();
            var code = g.Str("barcode").Trim();
            if (sku.Length > 0 && code.Length > 0) barcodes[sku] = code;
        }
        var lines = job.Arr("lines");
        if (query.Length == 0) return lines;
        return lines.Where(l => Paging.JobLineMatches(l, query, barcodes.GetValueOrDefault(l.Str("sku").Trim().ToLowerInvariant(), ""))).ToList();
    }

    private void RenderFbsLines()
    {
        var lines = FilteredLines();
        var (visible, page) = Paging.Slice(lines, _fbsLinesPage);
        _fbsLinesPage = page;
        FbsLinesGrid.ItemsSource = visible.Select(line =>
        {
            var url = line.Str("image_url");
            var sku = line.Str("sku");
            var name = line.Str("product_name");
            var order = line.Str("order_display", line.Str("order_id"));
            return new FbsLineRow
            {
                Id = line.Int("id"),
                Seq = line.Str("seq"),
                Photo = url.Length > 0 ? Thumb(url, 32) : null,
                Sku = sku,
                Name = name,
                Order = order,
                Status = Paging.LineStatusRu(line.Str("status")),
                Tip = string.Join(" · ", new[] { sku, name, order }.Where(s => s.Length > 0)),
            };
        }).ToList();
        FbsLinesPageLabel.Text = Paging.RangeLabel(lines.Count, _fbsLinesPage);
        FbsLinesPrev.IsEnabled = _fbsLinesPage > 0;
        FbsLinesNext.IsEnabled = _fbsLinesPage + 1 < Paging.PageCount(lines.Count);
    }

    private void OnFbsLinesPrev(object sender, RoutedEventArgs e) { if (_fbsLinesPage > 0) { _fbsLinesPage--; RenderFbsLines(); } }
    private void OnFbsLinesNext(object sender, RoutedEventArgs e)
    {
        if (_fbsLinesPage + 1 < Paging.PageCount(FilteredLines().Count)) { _fbsLinesPage++; RenderFbsLines(); }
    }

    private void OnFbsLinesSearch(object sender, RoutedEventArgs e)
    {
        _fbsLinesPage = 0;
        RenderFbsLines();
        if (FbsManualBox.IsChecked == true) RenderRemaining();
    }

    private void OnFbsLinesSearchKey(object sender, KeyEventArgs e)
    {
        _fbsSearchTimer?.Stop();
        _fbsSearchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _fbsSearchTimer.Tick += (_, _) =>
        {
            _fbsSearchTimer.Stop();
            OnFbsLinesSearch(sender, e);
        };
        _fbsSearchTimer.Start();
    }

    private void UpdateFbsCacheHint()
    {
        var job = _fbsJob;
        var jid = job?.IntOrNull("id");
        var ids = LabelCache.JobLineIds(job);
        if (jid is null || ids.Count == 0) { FbsCacheHint.Text = ""; return; }
        var (have, total) = LabelCache.CachedCount(jid.Value, ids);
        FbsCacheHint.Text = have >= total ? $"Ярлыки: {have}/{total} на диске"
            : have > 0 ? $"Ярлыки: {have}/{total} на диске" : "Ярлыки не скачаны";
        FbsCacheHint.Foreground = have >= total ? Brushes.DarkGreen : have > 0 ? Brushes.DarkOrange : Brushes.Firebrick;
    }

    private void OnFbsManualToggle(object sender, RoutedEventArgs e)
    {
        FbsManualPanel.Visibility = FbsManualBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (FbsManualBox.IsChecked == true) RenderRemaining();
        FocusScan();
    }

    private void OnFbsSkipMpToggle(object sender, RoutedEventArgs e)
    {
        _config.SkipMpConfirm = FbsSkipMpBox.IsChecked == true;
        _config.Save();
        if (_fbsJob is not null) RenderFbsJob();
    }

    private void RenderRemaining()
    {
        var groups = _fbsJob?.Arr("remaining_groups") ?? [];
        var query = FbsLinesSearch.Text.Trim();
        var rows = new List<RemainingRow>();
        for (var i = 0; i < groups.Count; i++)
        {
            var g = groups[i];
            if (query.Length > 0 && !Paging.RemainingMatches(g, query)) continue;
            var url = g.Str("image_url");
            rows.Add(new RemainingRow
            {
                Index = i,
                Photo = url.Length > 0 ? Thumb(url, 32) : null,
                Sku = g.Str("sku"),
                Name = g.Str("name"),
                Qty = g.Str("quantity"),
                Barcode = g.Str("barcode"),
            });
        }
        FbsRemainingGrid.ItemsSource = rows;
        if (rows.Count > 0)
        {
            FbsRemainingGrid.SelectedIndex = 0;
            OnRemainingSelected(this, new SelectionChangedEventArgs(DataGrid.SelectionChangedEvent, new List<object>(), new List<object>()));
        }
        else
        {
            _selectedGroup = null;
            ShowBarcode("");
        }
    }

    private void OnRemainingSelected(object sender, SelectionChangedEventArgs e)
    {
        if (FbsRemainingGrid.SelectedItem is not RemainingRow row || _fbsJob is null)
        {
            _selectedGroup = null;
            ShowBarcode("");
            return;
        }
        var groups = _fbsJob.Arr("remaining_groups");
        if (row.Index < 0 || row.Index >= groups.Count) return;
        _selectedGroup = groups[row.Index];
        ShowBarcode(_selectedGroup.Str("barcode"));
    }

    private void ShowBarcode(string code)
    {
        code = (code ?? "").Trim();
        if (code.Length == 0)
        {
            FbsBarcodeImage.Source = null;
            return;
        }
        try
        {
            using var bmp = BarcodeLabel.RenderCode128(code, 170, 70);
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            FbsBarcodeImage.Source = BytesToImage(ms.ToArray());
        }
        catch { FbsBarcodeImage.Source = null; }
    }

    private async void OnRemainingPick(object sender, RoutedEventArgs e)
    {
        if (_fbsJob is null || _selectedGroup is null) return;
        var jobId = _fbsJob.Int("id");
        var sku = _selectedGroup.Str("sku");
        var pid = _selectedGroup.IntOrNull("product_id");
        await HandleAllocateAsync(
            () => _client.FbsPickSkuAsync(jobId, sku, pid, FbsBatchBox.IsChecked == true, !LabelCache.JobReady(_fbsJob), FbsSkipMpBox.IsChecked == true),
            "Выделение SKU...",
            sku.ToLowerInvariant());
    }

    private void OnFbsScanKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _ = OnFbsScanAsync();
        }
    }

    private async void OnFbsScan(object sender, RoutedEventArgs e) => await OnFbsScanAsync();

    private async Task OnFbsScanAsync()
    {
        var code = FbsScanBox.Text.Trim();
        FbsScanBox.Text = "";
        if (code.Length == 0) return;
        if (_fbsJob is null)
        {
            MessageBox.Show(this, "Сначала откройте задание", "FBS");
            return;
        }
        var jobId = _fbsJob.Int("id");
        var scanKey = code.ToLowerInvariant();
        var actives = ActiveLines();
        if (actives.Count > 0 && FbsSkipMpBox.IsChecked != true)
        {
            await FbsRunAsync(async () =>
            {
                var payload = await _client.FbsScanLabelAsync(jobId, code);
                _fbsJob = payload.Obj("job") ?? _fbsJob;
                RenderFbsJob();
                var left = ActiveLines().Count;
                SetStatus(left > 0 ? $"Ярлык принят · осталось пропикать: {left}" : "Ярлык принят, строка закрыта");
                FocusScan();
            }, "Сверка ярлыка...");
            return;
        }
        if (FbsSkipMpBox.IsChecked == true && scanKey.Length > 0 && scanKey == _lastScanCode && _lastScanLineIds.Count > 0 && !SkuHasPending(_lastScanSku))
        {
            if (MessageBox.Show(this, "Распечатать заново?", "FBS", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                await ReprintLastAsync(jobId);
            FocusScan();
            return;
        }
        await HandleAllocateAsync(
            () => _client.FbsScanProductAsync(jobId, code, FbsBatchBox.IsChecked == true, !LabelCache.JobReady(_fbsJob), FbsSkipMpBox.IsChecked == true),
            "Пик товара...",
            scanKey);
    }

    private bool SkuHasPending(string sku)
    {
        var want = (sku ?? "").Trim().ToLowerInvariant();
        if (want.Length == 0 || _fbsJob is null) return false;
        if (_fbsJob.Arr("lines").Any(l => l.Str("sku").Trim().ToLowerInvariant() == want && l.Str("status") == "pending"))
            return true;
        return _fbsJob.Arr("remaining_groups").Any(g => g.Str("sku").Trim().ToLowerInvariant() == want && g.Int("quantity") > 0);
    }

    private async Task ReprintLastAsync(int jobId)
    {
        if (_lastScanLineIds.Count == 0)
        {
            MessageBox.Show(this, "Нет последней строки для перепечатки", "FBS");
            return;
        }
        await FbsRunAsync(async () =>
        {
            var pdfs = await ResolvePdfsAsync(jobId, _lastScanLineIds, null);
            foreach (var pdf in pdfs)
                GdiPrinter.PrintPdf(pdf, _config.LabelProfile());
            SetStatus($"Ярлык перепечатан ({pdfs.Count})");
            FocusScan();
        }, "Перепечатка...");
    }

    private async void OnFbsReprint(object sender, RoutedEventArgs e)
    {
        var actives = ActiveLines();
        if (_fbsJob is null || actives.Count == 0)
        {
            MessageBox.Show(this, "Нет активной строки", "FBS");
            return;
        }
        var jobId = _fbsJob.Int("id");
        var ids = actives.Select(x => x.Int("id")).ToList();
        await FbsRunAsync(async () =>
        {
            var pdfs = await ResolvePdfsAsync(jobId, ids, null);
            foreach (var pdf in pdfs)
                GdiPrinter.PrintPdf(pdf, _config.LabelProfile());
            SetStatus($"На повторную печать: {pdfs.Count} ярл.");
            FocusScan();
        }, "Перепечатка...");
    }

    private async void OnFbsCancelPrint(object sender, RoutedEventArgs e)
    {
        var active = ActiveLines().FirstOrDefault();
        if (_fbsJob is null || active is null)
        {
            MessageBox.Show(this, "Нет активной строки", "FBS");
            return;
        }
        await FbsRunAsync(async () =>
        {
            var payload = await _client.FbsCancelPrintAsync(_fbsJob.Int("id"), active.Int("id"));
            _fbsJob = payload.Obj("job") ?? _fbsJob;
            RenderFbsJob();
            SetStatus("Печать отменена, КИЗ сброшен, строки снова в сборке");
            FocusScan();
        }, "Отмена...");
    }

    private async void OnDownloadFbsLabels(object sender, RoutedEventArgs e)
    {
        if (_fbsJob is null)
        {
            MessageBox.Show(this, "Сначала откройте задание", "FBS");
            return;
        }
        if (!RequireApi()) return;
        await PrefetchLabelsAsync(_fbsJob, true);
    }

    private async Task PrefetchLabelsAsync(JsonMap job, bool interactive)
    {
        var jobId = job.Int("id");
        if (jobId == 0) return;
        if (_prefetchJobId == jobId && !interactive) return;
        if (LabelCache.JobReady(job) && !interactive)
        {
            UpdateFbsCacheHint();
            return;
        }
        if (interactive)
        {
            if (_fbsBusy) return;
            _fbsBusy = true;
            SetStatus("Скачивание ярлыков...");
        }
        _prefetchJobId = jobId;
        try
        {
            var zip = await _client.FbsDownloadLineLabelsZipAsync(jobId);
            var saved = LabelCache.SaveZip(jobId, zip);
            UpdateFbsCacheHint();
            SetStatus($"Ярлыки сохранены локально: {saved}");
            if (interactive) FocusScan();
        }
        catch (Exception ex)
        {
            if (interactive) ShowFbsError(ex);
            else
            {
                FbsCacheHint.Text = "Не удалось скачать ярлыки";
                FbsCacheHint.Foreground = Brushes.Firebrick;
            }
        }
        finally
        {
            if (interactive) _fbsBusy = false;
            if (_prefetchJobId == jobId) _prefetchJobId = null;
        }
    }

    private async Task<List<byte[]>> ResolvePdfsAsync(int jobId, List<int> lineIds, List<string>? payloadPdfs)
    {
        var pdfs = new List<byte[]>();
        for (var i = 0; i < lineIds.Count; i++)
        {
            var cached = LabelCache.GetLine(jobId, lineIds[i]);
            if (cached != null) { pdfs.Add(cached); continue; }
            if (payloadPdfs != null && i < payloadPdfs.Count && payloadPdfs[i].Length > 0)
            {
                var pdf = Convert.FromBase64String(payloadPdfs[i]);
                LabelCache.PutLine(jobId, lineIds[i], pdf);
                pdfs.Add(pdf);
                continue;
            }
            var downloaded = await _client.FbsDownloadLinePdfAsync(jobId, lineIds[i]);
            LabelCache.PutLine(jobId, lineIds[i], downloaded);
            pdfs.Add(downloaded);
        }
        return pdfs;
    }

    private async Task HandleAllocateAsync(Func<Task<JsonMap>> worker, string status, string scanCode)
    {
        if (_fbsJob is null) return;
        var jobId = _fbsJob.Int("id");
        var skip = FbsSkipMpBox.IsChecked == true;
        var profile = _config.LabelProfile();
        await FbsRunAsync(async () =>
        {
            var payload = await worker();
            var lines = payload.Arr("lines");
            if (lines.Count == 0 && payload.Obj("line") is { } one) lines = [one];
            var payloadPdfs = payload.StrList("pdfs_base64");
            if (payloadPdfs.Count == 0 && payload.Str("pdf_base64").Length > 0)
                payloadPdfs = [payload.Str("pdf_base64")];
            var lineIds = lines.Where(l => l.IntOrNull("id") is not null).Select(l => l.Int("id")).ToList();
            var pdfs = jobId > 0 ? await ResolvePdfsAsync(jobId, lineIds, payloadPdfs) : [];
            var printed = 0;
            Exception? printError = null;
            Exception? closeError = null;
            try
            {
                foreach (var pdf in pdfs)
                {
                    GdiPrinter.PrintPdf(pdf, profile);
                    printed++;
                }
            }
            catch (Exception ex) { printError = ex; }
            var needClose = lines.Where(l => l.IntOrNull("id") is not null && l.Str("status") != "done").ToList();
            if (skip && jobId > 0 && needClose.Count > 0)
            {
                try
                {
                    JsonMap? closed = null;
                    foreach (var closeLine in needClose)
                        closed = await _client.FbsCloseLineAsync(jobId, closeLine.Int("id"));
                    if (closed is not null) payload = closed;
                }
                catch (Exception ex) { closeError = ex; }
            }
            _fbsJob = payload.Obj("job") ?? _fbsJob;
            var closedLines = payload.Arr("lines");
            if (closedLines.Count == 0 && payload.Obj("line") is { } cl) closedLines = [cl];
            var line = closedLines.FirstOrDefault() ?? lines.FirstOrDefault();
            if (skip && lineIds.Count > 0)
            {
                _lastScanCode = scanCode.Length > 0 ? scanCode : (line?.Str("sku") ?? "").ToLowerInvariant();
                _lastScanSku = line?.Str("sku") ?? "";
                _lastScanLineIds = lineIds;
            }
            RenderFbsJob();
            if (printError is not null)
            {
                MessageBox.Show(this, printError.Message, "Печать", MessageBoxButton.OK, MessageBoxImage.Error);
                SetStatus($"Получено ярлыков: {printed}. Можно Перепечатать");
                FocusScan();
                return;
            }
            if (closeError is not null)
                MessageBox.Show(this, $"Ярлык напечатан, но строка не закрыта: {closeError.Message}", "FBS", MessageBoxButton.OK, MessageBoxImage.Warning);
            var sku = line?.Str("sku") ?? "";
            var cis = line?.Flag("has_cis") == true ? " · КИЗ записан" : "";
            if (printed > 1)
                SetStatus(skip ? $"Готово · SKU {sku} · ярлыков {printed} · пикайте следующий"
                    : $"Напечатано ярлыков: {printed} · SKU {sku} · пропикайте ярлыки подряд");
            else if (printed == 1)
                SetStatus(skip
                    ? $"Готово · SKU {line?.Str("sku")} · заказ {line?.Str("order_id")}{cis} · пикайте следующий"
                    : $"Напечатан ярлык · SKU {line?.Str("sku")} · заказ {line?.Str("order_id")}{cis}");
            else
                SetStatus("Строка выделена");
            FocusScan();
        }, status);
    }

    private async void OnFbsLineContext(object sender, MouseButtonEventArgs e)
    {
        if (FbsLinesGrid.SelectedItem is not FbsLineRow row || _fbsJob is null) return;
        var line = _fbsJob.Arr("lines").FirstOrDefault(l => l.Int("id") == row.Id);
        if (line is null) return;
        var status = line.Str("status");
        var menu = new ContextMenu();
        var item = new MenuItem { Header = status is "printed" or "done" ? "Статус: в сборке" : "Статус: готово" };
        var next = status is "printed" or "done" ? "pending" : "done";
        item.Click += async (_, _) =>
        {
            if (!RequireApi()) return;
            await FbsRunAsync(async () =>
            {
                var payload = await _client.FbsSetLineStatusAsync(_fbsJob.Int("id"), row.Id, next);
                _fbsJob = payload.Obj("job") ?? _fbsJob;
                RenderFbsJob();
                SetStatus($"Статус строки: {Paging.LineStatusRu(next)}");
                FocusScan();
            }, "Смена статуса...");
        };
        menu.Items.Add(item);
        menu.IsOpen = true;
    }

    private async Task FbsRunAsync(Func<Task> work, string status)
    {
        if (_fbsBusy) return;
        _fbsBusy = true;
        SetStatus(status);
        try { await work(); }
        catch (Exception ex) { ShowFbsError(ex); }
        finally { _fbsBusy = false; }
    }

    private void ShowFbsError(Exception ex)
    {
        if (ex is AuthException) { ShowError(ex); return; }
        if (ex is ApiException)
        {
            MessageBox.Show(this, ex.Message, "FBS", MessageBoxButton.OK, MessageBoxImage.Warning);
            SetStatus(ex.Message);
            FocusScan();
            return;
        }
        ShowError(ex);
        FocusScan();
    }
}
