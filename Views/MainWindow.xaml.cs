using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
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
    private DispatcherTimer? _fbsSearchTimer;

    private readonly PhotoLoader _photos;
    private readonly ObservableCollection<TaskRow> _taskRows = [];
    private readonly ObservableCollection<AttachmentRow> _fileRows = [];
    private readonly ObservableCollection<CatalogRow> _catalogRows = [];
    private readonly ObservableCollection<FbsJobRow> _fbsJobRows = [];
    private readonly ObservableCollection<FbsLineRow> _fbsLineRows = [];
    private readonly ObservableCollection<RemainingRow> _remainingRows = [];

    private CancellationTokenSource? _fbsOpenCts;
    private readonly DispatcherTimer _fbsSelectTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private int _fbsPendingJobId;
    private int _cacheHintJobId;
    private int _cachedLabelsHave;
    private int _cachedLabelsTotal;
    private string _activeImageUrl = "";

    private bool _fboBusy;
    private bool _fboJobsFilling;
    private List<JsonMap> _fboJobs = [];
    private JsonMap? _fboJob;
    private int _fboJobsPage;
    private int _fboLinesPage;
    private object? _fboPagedJobId;
    private JsonMap? _fboSelectedGroup;
    private string _fboLastScanCode = "";
    private string _fboLastScanSku = "";
    private List<int> _fboLastScanLineIds = [];
    private readonly ObservableCollection<FbsJobRow> _fboJobRows = [];
    private readonly ObservableCollection<FbsLineRow> _fboLineRows = [];
    private readonly ObservableCollection<RemainingRow> _fboRemainingRows = [];
    private CancellationTokenSource? _fboOpenCts;
    private readonly DispatcherTimer _fboSelectTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private int _fboPendingJobId;
    private string _fboActiveImageUrl = "";
    private string _fboQtyWarning = "";

    private bool _fboNewBusy;
    private bool _fboNewJobsFilling;
    private List<JsonMap> _fboNewJobs = [];
    private JsonMap? _fboNewJob;
    private JsonMap? _fboNewProduct;
    private int _fboNewJobsPage;
    private int _fboNewPendingJobId;
    private int _fboNewLastPrintedBoxId;
    private int _fboNewLastPrintedPalletId;
    private bool _fboNewClosingPallet;
    private string _fboNewQtyWarning = "";
    private readonly Dictionary<string, int> _fboNewRememberedQty = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _fboNewProductionDates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<FbsJobRow> _fboNewJobRows = [];
    private readonly ObservableCollection<RemainingRow> _fboNewRemainingRows = [];
    private readonly ObservableCollection<FboOverviewGroupRow> _fboNewByProductRows = [];
    private readonly ObservableCollection<FboOverviewGroupRow> _fboNewByCargoRows = [];
    private readonly ObservableCollection<FboOverviewGroupRow> _fboNewByPalletRows = [];
    private bool _fboNewOverviewOpen;
    private CancellationTokenSource? _fboNewOpenCts;
    private readonly DispatcherTimer _fboNewSelectTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    public MainWindow(AppConfig config, ApiClient client, string userName)
    {
        InitializeComponent();
        _config = config;
        _client = client;
        _photos = new PhotoLoader(Dispatcher);
        Title = $"Warehouse Packing — {userName}";
        WindowState = WindowState.Maximized;
        FbsSkipMpBox.IsChecked = config.SkipMpConfirm;
        FboSkipMpBox.IsChecked = config.SkipMpConfirm;

        // Assigned once: mutating the collections keeps user column widths and
        // the current selection, re-assigning ItemsSource would reset both.
        TasksGrid.ItemsSource = _taskRows;
        FilesGrid.ItemsSource = _fileRows;
        CatalogGrid.ItemsSource = _catalogRows;
        FbsJobsGrid.ItemsSource = _fbsJobRows;
        FbsLinesGrid.ItemsSource = _fbsLineRows;
        FbsRemainingGrid.ItemsSource = _remainingRows;
        FboJobsGrid.ItemsSource = _fboJobRows;
        FboLinesGrid.ItemsSource = _fboLineRows;
        FboRemainingGrid.ItemsSource = _fboRemainingRows;
        FboNewJobsGrid.ItemsSource = _fboNewJobRows;
        FboNewRemainingGrid.ItemsSource = _fboNewRemainingRows;
        FboNewByProductList.ItemsSource = _fboNewByProductRows;
        FboNewByCargoList.ItemsSource = _fboNewByCargoRows;
        FboNewByPalletList.ItemsSource = _fboNewByPalletRows;
        OmJobsGrid.ItemsSource = _omJobRows;
        OmLinesGrid.ItemsSource = _omLineRows;
        OmRemainingGrid.ItemsSource = _omRemainingRows;

        _tasksTimer.Tick += (_, _) =>
        {
            if (Tabs.SelectedIndex == 4)
                _ = LoadTasksAsync(true);
        };
        _fbsSelectTimer.Tick += (_, _) =>
        {
            _fbsSelectTimer.Stop();
            if (_fbsPendingJobId > 0)
                _ = OpenFbsJobAsync(_fbsPendingJobId);
        };
        _fboSelectTimer.Tick += (_, _) =>
        {
            _fboSelectTimer.Stop();
            if (_fboPendingJobId > 0)
                _ = OpenFboJobAsync(_fboPendingJobId);
        };
        _fboNewSelectTimer.Tick += (_, _) =>
        {
            _fboNewSelectTimer.Stop();
            if (_fboNewPendingJobId > 0)
                _ = OpenFboNewJobAsync(_fboNewPendingJobId);
        };
        Loaded += async (_, _) => await LoadFboJobsAsync();
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private async void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _tasksTimer.Stop();
        _fbsSelectTimer.Stop();
        _fboSelectTimer.Stop();
        _fboNewSelectTimer.Stop();
        _fbsOpenCts?.Cancel();
        _fboOpenCts?.Cancel();
        _fboNewOpenCts?.Cancel();
        await _client.LogoutAsync();
        _client.Dispose();
    }

    private void OnLogout(object sender, RoutedEventArgs e) => Close();

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow(_config, _client) { Owner = this, CacheHintChanged = InvalidateCacheHint };
        if (dlg.ShowDialog() == true)
        {
            _config = AppConfig.Load();
            FbsSkipMpBox.IsChecked = _config.SkipMpConfirm;
            SetStatus("Настройки сохранены");
            RestartTasksTimer();
        }
    }

    /// Selector.SelectionChanged bubbles, so picking a grid row or a combo box
    /// item inside a tab also raises this event on the TabControl. Reloading
    /// the tab from there rebuilt the grid under the mouse and swallowed the
    /// click that started it.
    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || !ReferenceEquals(e.OriginalSource, Tabs)) return;
        switch (Tabs.SelectedIndex)
        {
            case 0:
                _tasksTimer.Stop();
                _ = LoadFboJobsAsync();
                break;
            case 1:
                _tasksTimer.Stop();
                _ = LoadFboNewJobsAsync();
                break;
            case 2:
                _tasksTimer.Stop();
                _ = LoadFbsJobsAsync();
                break;
            case 3:
                _tasksTimer.Stop();
                _ = LoadCatalogAsync(false);
                break;
            case 5:
                _tasksTimer.Stop();
                _ = LoadOtherMpJobsAsync();
                break;
        }
    }

    private void RestartTasksTimer()
    {
        _tasksTimer.Stop();
        _tasksTimer.Interval = TimeSpan.FromMilliseconds(_config.RefreshMs);
        if (Tabs.SelectedIndex == 4)
            _tasksTimer.Start();
    }

    private static T? FindParent<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null and not T)
            source = source is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        return source as T;
    }

    /// A right-click does not move the DataGrid selection on its own, so the
    /// context menu would act on whatever was selected before.
    private void OnGridRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid)
            return;
        var row = FindParent<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row is null)
            return;
        grid.SelectedItem = row.Item;
        row.IsSelected = true;
    }

    private void ShowError(Exception ex)
    {
        if (ex is OperationCanceledException)
            return;
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
        if (ex is HttpRequestException or IOException or SocketException)
        {
            MessageBox.Show(this, "Нет связи с сервером. Повторите пик.", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            SetStatus("Нет связи с сервером");
            return;
        }
        MessageBox.Show(this, ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        SetStatus("Ошибка");
    }

    private async Task RunPrint(string status, Func<Task> work)
    {
        if (_printBusy)
        {
            SetStatus("Печать уже выполняется — дождитесь окончания");
            return;
        }
        _printBusy = true;
        SetStatus(status);
        try { await Task.Run(work); }
        catch (Exception ex) { ShowError(ex); }
        finally { _printBusy = false; }
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
        _taskRows.Clear();
        foreach (var t in _tasks)
        {
            _taskRows.Add(new TaskRow
            {
                Id = t.Int("id"),
                Assembly = Paging.FormatDay(t.Str("start_date")),
                Marketplace = t.Str("counterparty_name", "—"),
                Ship = Paging.FormatDay(t.Str("end_date")),
                Tag = Paging.ShipTag(t.Str("end_date")),
            });
        }
        if (selected is int sid)
        {
            var row = _taskRows.FirstOrDefault(x => x.Id == sid);
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
        _fileRows.Clear();
        foreach (var att in task?.Arr("attachments") ?? [])
        {
            var kind = att.Str("kind");
            _fileRows.Add(new AttachmentRow
            {
                Id = att.Int("id"),
                Kind = kind == "a4" ? "А4" : kind == "label" ? "Этикетки" : kind,
                Filename = att.Str("filename", "file.pdf"),
            });
        }
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

    private async void OnPrintAttachmentButton(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { DataContext: AttachmentRow row }) return;
        FilesGrid.SelectedItem = row;
        SetStatus($"Печать: {row.Filename}...");
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
            await Task.Run(() => GdiPrinter.PrintPdf(pdf, profile));
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
            var pdfs = new List<byte[]>();
            foreach (var att in items)
                pdfs.Add(await _client.DownloadTaskAttachmentAsync(taskId, att.Int("id")));
            await Task.Run(() => GdiPrinter.PrintPdfs(pdfs, profile));
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
            var a4Pdfs = new List<byte[]>();
            foreach (var att in a4)
                a4Pdfs.Add(await _client.DownloadTaskAttachmentAsync(taskId, att.Int("id")));
            var labelPdfs = new List<byte[]>();
            foreach (var att in labels)
                labelPdfs.Add(await _client.DownloadTaskAttachmentAsync(taskId, att.Int("id")));
            if (a4Pdfs.Count > 0)
                await Task.Run(() => GdiPrinter.PrintPdfs(a4Pdfs, a4p));
            if (labelPdfs.Count > 0)
                await Task.Run(() => GdiPrinter.PrintPdfs(labelPdfs, lp));
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
        _catalogRows.Clear();
        foreach (var p in visible)
        {
            var id = p.Int("id");
            if (id == 0) continue;
            var name = p.Str("name");
            if (p.Flag("is_kit")) name += " (комплект)";
            var url = p.Str("image_url").Trim();
            _catalogImageUrls[id.ToString()] = url;
            var row = new CatalogRow { Id = id, Sku = p.Str("sku"), Name = name };
            _catalogRows.Add(row);
            _photos.Load(url, 40, img => row.Photo = img);
        }
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

    private async void OnCatalogPrintButton(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { DataContext: CatalogRow row }) return;
        CatalogGrid.SelectedItem = row;
        SetStatus($"Печать ШК: {row.Sku}...");
        await PrintCatalogBarcodeAsync(row.Id);
    }

    private async void OnCatalogPrintMenu(object sender, RoutedEventArgs e)
    {
        if (CatalogGrid.SelectedItem is not CatalogRow row)
        {
            SetStatus("Выберите товар в списке");
            return;
        }
        SetStatus($"Печать ШК: {row.Sku}...");
        await PrintCatalogBarcodeAsync(row.Id);
    }

    private async void OnCatalogAddBarcodeMenu(object sender, RoutedEventArgs e)
    {
        if (CatalogGrid.SelectedItem is not CatalogRow row)
        {
            SetStatus("Выберите товар в списке");
            return;
        }
        SetStatus($"Добавление ШК: {row.Sku}...");
        await AddCatalogBarcodeAsync(row.Id);
    }

    private async void OnCatalogAddGtinMenu(object sender, RoutedEventArgs e)
    {
        if (CatalogGrid.SelectedItem is not CatalogRow row)
        {
            SetStatus("Выберите товар в списке");
            return;
        }
        SetStatus($"Добавление GTIN: {row.Sku}...");
        await AddCatalogGtinAsync(row.Id);
    }

    private async void OnCatalogAddBoxMenu(object sender, RoutedEventArgs e)
    {
        if (CatalogGrid.SelectedItem is not CatalogRow row)
        {
            SetStatus("Выберите товар в списке");
            return;
        }
        SetStatus($"Добавление ШК короба: {row.Sku}...");
        await AddCatalogBoxAsync(row.Id);
    }

    /// Without a row under the cursor the menu would open and then do nothing.
    private void OnCatalogContextOpening(object sender, ContextMenuEventArgs e)
    {
        if (CatalogGrid.SelectedItem is not CatalogRow)
        {
            SetStatus("Нажмите правой кнопкой на строку товара");
            e.Handled = true;
        }
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
        var chosen = PickBarcode(barcodes);
        if (chosen is null) return;
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
        await RunPrint("Печать штрихкода...", async () =>
        {
            var pdf = BarcodeLabel.LabelPdf(barcode, sku, name, size.WidthMm, size.HeightMm);
            await Task.Run(() => GdiPrinter.PrintPdf(pdf, profile, copies));
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

    private async Task AddCatalogGtinAsync(int productId)
    {
        var product = CatalogById(productId);
        if (product is null)
        {
            MessageBox.Show(this, "Товар не найден — обновите список", "Нет товара");
            return;
        }
        var dlg = new AddGtinWindow(product.Str("sku"), product.Str("name")) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        SetStatus("Сохранение GTIN...");
        try
        {
            var payload = await _client.AddCatalogGtinAsync(productId, dlg.CodeText);
            var gtin = payload.Str("gtin");
            SetStatus(payload.Str("action") == "exists"
                ? $"GTIN {gtin} уже был у {product.Str("sku")}"
                : $"GTIN {gtin} добавлен к {product.Str("sku")}");
        }
        catch (ApiException ex)
        {
            MessageBox.Show(this, ex.Message, "GTIN", MessageBoxButton.OK, MessageBoxImage.Error);
            SetStatus(ex.Message);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async Task AddCatalogBoxAsync(int productId)
    {
        var product = CatalogById(productId);
        if (product is null)
        {
            MessageBox.Show(this, "Товар не найден — обновите список", "Нет товара");
            return;
        }
        var dlg = new AddBoxWindow(product.Str("sku"), product.Str("name")) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        SetStatus("Сохранение ШК короба...");
        try
        {
            var payload = await _client.AddCatalogBoxAsync(productId, dlg.BarcodeText, dlg.Quantity);
            var barcode = payload.Str("barcode");
            if (barcode.Length == 0)
                barcode = dlg.BarcodeText;
            var qty = payload.Int("quantity");
            if (qty <= 0)
                qty = dlg.Quantity;
            var action = payload.Str("action");
            var sku = product.Str("sku");
            SetStatus(action switch
            {
                "exists" => $"ШК короба {barcode} уже был у {sku}",
                "updated" => $"ШК короба {barcode}: количество обновлено на {qty} у {sku}",
                _ => $"ШК короба {barcode} ({qty} шт.) добавлен к {sku}",
            });
        }
        catch (ApiException ex)
        {
            MessageBox.Show(this, ex.Message, "ШК короба", MessageBoxButton.OK, MessageBoxImage.Error);
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
            var selected = _fbsPendingJobId > 0 ? _fbsPendingJobId : _fbsJob?.IntOrNull("id");
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
        _fbsJobRows.Clear();
        foreach (var j in visible)
        {
            _fbsJobRows.Add(new FbsJobRow
            {
                Id = j.Int("id"),
                Status = Paging.JobStatusRu(j.Str("status")),
                Progress = $"{j.Int("line_done")}/{j.Int("line_total")}",
            });
        }
        // A refresh that lands while a job is being opened must not drag the
        // selection back to the previous job.
        var selected = _fbsPendingJobId > 0 ? _fbsPendingJobId : _fbsJob?.IntOrNull("id");
        if (selected is int sid)
            FbsJobsGrid.SelectedItem = _fbsJobRows.FirstOrDefault(x => x.Id == sid);
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

    /// Clicking through the list must not fire a request per click: opening a
    /// job is expensive on the server, and dozens of them queue up and stall
    /// every later open.
    private void OnFbsJobSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_fbsJobsFilling || FbsJobsGrid.SelectedItem is not FbsJobRow row) return;
        _fbsPendingJobId = row.Id;
        SetStatus($"Задание #{row.Id}...");
        _fbsSelectTimer.Stop();
        _fbsSelectTimer.Start();
    }

    /// Switching jobs abandons the previous request instead of queueing behind
    /// it, so clicking through the list stays responsive.
    private async Task OpenFbsJobAsync(int jobId)
    {
        var previous = _fbsOpenCts;
        var cts = new CancellationTokenSource();
        _fbsOpenCts = cts;
        previous?.Cancel();
        previous?.Dispose();
        SetStatus($"Открытие задания #{jobId}...");
        try
        {
            var job = await _client.FbsOpenJobAsync(jobId, cts.Token);
            if (!ReferenceEquals(_fbsOpenCts, cts)) return;
            _fbsJob = job;
            RenderFbsJob();
            SetStatus($"FBS задание #{job.Str("id")} · строк {job.Arr("lines").Count}");
            FocusScan();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (ReferenceEquals(_fbsOpenCts, cts)) ShowFbsError(ex);
        }
        finally
        {
            if (ReferenceEquals(_fbsOpenCts, cts))
            {
                _fbsOpenCts = null;
                cts.Dispose();
            }
        }
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

    private void SetActiveImage(string url)
    {
        var raw = (url ?? "").Trim();
        _activeImageUrl = raw;
        FbsActiveImage.Source = null;
        if (raw.Length == 0) return;
        _photos.Load(raw, 110, img =>
        {
            if (_activeImageUrl == raw) FbsActiveImage.Source = img;
        });
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
        _fbsLineRows.Clear();
        foreach (var line in visible)
        {
            var sku = line.Str("sku");
            var name = line.Str("product_name");
            var order = line.Str("order_display", line.Str("order_id"));
            var row = new FbsLineRow
            {
                Id = line.Int("id"),
                Seq = line.Str("seq"),
                Sku = sku,
                Name = name,
                Order = order,
                Status = Paging.LineStatusRu(line.Str("status")),
            };
            _fbsLineRows.Add(row);
            _photos.Load(line.Str("image_url"), 34, img => row.Photo = img);
        }
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

    private void InvalidateCacheHint()
    {
        _cacheHintJobId = 0;
        UpdateFbsCacheHint();
    }

    /// Counting cached labels touches one file per line, so it runs off the UI
    /// thread and only when the job (or the cache) actually changed.
    private async void UpdateFbsCacheHint()
    {
        var job = _fbsJob;
        var jid = job?.IntOrNull("id");
        if (jid is null)
        {
            FbsCacheHint.Text = "";
            _cacheHintJobId = 0;
            _cachedLabelsHave = _cachedLabelsTotal = 0;
            return;
        }
        var jobId = jid.Value;
        if (_cacheHintJobId == jobId) return;
        _cacheHintJobId = jobId;
        var ids = LabelCache.JobLineIds(job);
        if (ids.Count == 0)
        {
            FbsCacheHint.Text = "";
            _cachedLabelsHave = _cachedLabelsTotal = 0;
            return;
        }
        int have, total;
        try { (have, total) = await Task.Run(() => LabelCache.CachedCount(jobId, ids)); }
        catch { return; }
        if (_cacheHintJobId != jobId) return;
        _cachedLabelsHave = have;
        _cachedLabelsTotal = total;
        FbsCacheHint.Text = have > 0 ? $"Ярлыки: {have}/{total} на диске" : "Ярлыки не скачаны";
        FbsCacheHint.Foreground = have >= total ? Brushes.DarkGreen : have > 0 ? Brushes.DarkOrange : Brushes.Firebrick;
    }

    private bool LabelsCached => _cachedLabelsTotal > 0 && _cachedLabelsHave >= _cachedLabelsTotal;

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
        _remainingRows.Clear();
        for (var i = 0; i < groups.Count; i++)
        {
            var g = groups[i];
            if (query.Length > 0 && !Paging.RemainingMatches(g, query)) continue;
            var row = new RemainingRow
            {
                Index = i,
                Sku = g.Str("sku"),
                Name = g.Str("name"),
                Qty = g.Str("quantity"),
                Barcode = g.Str("barcode"),
            };
            _remainingRows.Add(row);
            _photos.Load(g.Str("image_url"), 34, img => row.Photo = img);
        }
        if (_remainingRows.Count > 0)
        {
            FbsRemainingGrid.SelectedIndex = 0;
            SyncSelectedGroup();
        }
        else
        {
            _selectedGroup = null;
            ShowBarcode("");
        }
    }

    private void OnRemainingSelected(object sender, SelectionChangedEventArgs e) => SyncSelectedGroup();

    private void SyncSelectedGroup()
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
            FbsBarcodeImage.Source = PhotoLoader.Decode(ms.ToArray());
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
            () => _client.FbsPickSkuAsync(jobId, sku, pid, FbsBatchBox.IsChecked == true, !LabelsCached, FbsSkipMpBox.IsChecked == true),
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
            () => _client.FbsScanProductAsync(jobId, code, FbsBatchBox.IsChecked == true, !LabelsCached, FbsSkipMpBox.IsChecked == true),
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
            if (pdfs.Count > 0)
                await Task.Run(() => GdiPrinter.PrintPdfs(pdfs, _config.LabelProfile()));
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
            if (pdfs.Count > 0)
                await Task.Run(() => GdiPrinter.PrintPdfs(pdfs, _config.LabelProfile()));
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
        await DownloadLabelsAsync(_fbsJob);
    }

    /// Only ever on request: zipping the labels of a large job keeps the
    /// server busy long enough to stall the next job you open.
    private async Task DownloadLabelsAsync(JsonMap job)
    {
        var jobId = job.Int("id");
        if (jobId == 0 || _fbsBusy) return;
        _fbsBusy = true;
        SetStatus("Скачивание ярлыков...");
        try
        {
            var zip = await _client.FbsDownloadLineLabelsZipAsync(jobId);
            var saved = await Task.Run(() => LabelCache.SaveZip(jobId, zip));
            InvalidateCacheHint();
            SetStatus($"Ярлыки сохранены локально: {saved}");
            FocusScan();
        }
        catch (Exception ex) { ShowFbsError(ex); }
        finally { _fbsBusy = false; }
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
                if (pdfs.Count > 0)
                {
                    await Task.Run(() => GdiPrinter.PrintPdfs(pdfs, profile));
                    printed = pdfs.Count;
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

    private JsonMap? SelectedLine()
    {
        if (FbsLinesGrid.SelectedItem is not FbsLineRow row || _fbsJob is null) return null;
        return _fbsJob.Arr("lines").FirstOrDefault(l => l.Int("id") == row.Id);
    }

    private void OnFbsLinesContextOpening(object sender, ContextMenuEventArgs e)
    {
        var line = SelectedLine();
        if (line is null || FbsLinesGrid.ContextMenu?.Items.Count is not > 0)
        {
            e.Handled = true;
            return;
        }
        if (FbsLinesGrid.ContextMenu.Items[0] is MenuItem item)
            item.Header = line.Str("status") is "printed" or "done" ? "Статус: в сборке" : "Статус: готово";
    }

    private async void OnFbsLineStatusToggle(object sender, RoutedEventArgs e)
    {
        var line = SelectedLine();
        if (line is null || _fbsJob is null) return;
        if (!RequireApi()) return;
        var lineId = line.Int("id");
        var next = line.Str("status") is "printed" or "done" ? "pending" : "done";
        await FbsRunAsync(async () =>
        {
            var payload = await _client.FbsSetLineStatusAsync(_fbsJob.Int("id"), lineId, next);
            _fbsJob = payload.Obj("job") ?? _fbsJob;
            RenderFbsJob();
            SetStatus($"Статус строки: {Paging.LineStatusRu(next)}");
            FocusScan();
        }, "Смена статуса...");
    }

    private async Task FbsRunAsync(Func<Task> work, string status)
    {
        if (_fbsBusy)
        {
            SetStatus("Предыдущая операция ещё выполняется");
            return;
        }
        _fbsBusy = true;
        SetStatus(status);
        try { await work(); }
        catch (Exception ex) { ShowFbsError(ex); }
        finally { _fbsBusy = false; }
    }

    private void ShowFbsError(Exception ex)
    {
        if (ex is OperationCanceledException) return;
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
