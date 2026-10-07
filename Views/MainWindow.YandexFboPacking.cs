using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WarehousePacking.Models;
using WarehousePacking.Services;

namespace WarehousePacking.Views;

public partial class MainWindow
{
    private const string FboPlatformWb = "wildberries";
    private const string FboPlatformYm = "yandex";

    private bool _ymBusy;
    private JsonMap? _ymJob;
    private JsonMap? _ymProduct;
    private string _ymActiveImageUrl = "";
    private readonly ObservableCollection<RemainingRow> _ymRemainingRows = [];
    private readonly ObservableCollection<RemainingRow> _ymCargoRows = [];
    private CancellationTokenSource? _ymOpenCts;

    private static string FboJobPlatform(JsonMap job)
    {
        var marketplace = job.Str("marketplace");
        if (marketplace.Length > 0) return marketplace;
        var platform = job.Str("platform");
        return platform.Length > 0 ? platform : FboPlatformWb;
    }

    private static bool IsYmJob(JsonMap job) =>
        FboJobPlatform(job).Equals(FboPlatformYm, StringComparison.OrdinalIgnoreCase);

    private static string FboPlatformLabel(JsonMap job) => IsYmJob(job) ? "YM" : "WB";

    private void ApplyFboMarketplaceUi(string platform)
    {
        var ym = platform.Equals(FboPlatformYm, StringComparison.OrdinalIgnoreCase);
        FboWbPanel.Visibility = ym ? Visibility.Collapsed : Visibility.Visible;
        FboYmPanel.Visibility = ym ? Visibility.Visible : Visibility.Collapsed;
        FboWbDownloadLabels.Visibility = ym ? Visibility.Collapsed : Visibility.Visible;
        FboWbPrintQr.Visibility = ym ? Visibility.Collapsed : Visibility.Visible;
        FboWbPrintSheets.Visibility = ym ? Visibility.Collapsed : Visibility.Visible;
        FboYmPrintCargos.Visibility = ym ? Visibility.Visible : Visibility.Collapsed;
        if (ym) FocusYmScan();
        else FocusFboScan();
    }

    private void FocusYmScan() => FboYmScanBox.Focus();

    private void FocusYmQty()
    {
        FboYmQtyBox.Focus();
        FboYmQtyBox.SelectAll();
    }

    private async Task OpenSelectedFboJobAsync()
    {
        if (_fboPendingJobId <= 0) return;
        if (_fboPendingPlatform.Equals(FboPlatformYm, StringComparison.OrdinalIgnoreCase))
            await OpenYmJobAsync(_fboPendingJobId);
        else
            await OpenFboJobAsync(_fboPendingJobId);
    }

    private async Task OpenYmJobAsync(int jobId)
    {
        var previous = _ymOpenCts;
        var cts = new CancellationTokenSource();
        _ymOpenCts = cts;
        previous?.Cancel();
        SetStatus($"Открытие задания YM #{jobId}...");
        try
        {
            var job = await _client.YandexFboOpenJobAsync(jobId, cts.Token);
            if (!ReferenceEquals(_ymOpenCts, cts)) return;
            _ymJob = job;
            _ymProduct = null;
            ApplyFboMarketplaceUi(FboPlatformYm);
            RenderYmJob();
            FocusYmScan();
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_ymOpenCts, cts)) ShowYmError(ex);
        }
        finally
        {
            if (ReferenceEquals(_ymOpenCts, cts))
            {
                _ymOpenCts = null;
                cts.Dispose();
            }
        }
    }

    private void RenderYmJob()
    {
        var job = _ymJob;
        var title = job is null
            ? "Выберите задание"
            : $"YM {job.Str("display_id", job.Str("supply_id"))} · #{job.Str("id")}";
        if (job is not null && job.Str("warehouse_name").Length > 0)
            title += $" · {job.Str("warehouse_name")}";
        FboYmJobTitle.Text = title;
        var done = job?.Int("line_done") ?? 0;
        var total = job?.Int("line_total") ?? 0;
        var pending = job?.Int("line_pending", job?.Int("remaining") ?? 0) ?? 0;
        FboYmJobStats.Text = $"готово {done}/{total} · осталось {pending}";
        RenderYmProduct();
        RenderYmRemaining();
        RenderYmCargos();
    }

    private void RenderYmProduct()
    {
        var product = _ymProduct;
        if (product is null)
        {
            FboYmActiveText.Text = "Сначала отсканируйте товар";
            SetYmActiveImage("");
            return;
        }
        var remaining = product.Int("remaining_qty", product.Int("remaining"));
        FboYmActiveText.Text =
            $"{product.Str("sku")} · {product.Str("name", product.Str("product_name"))} · остаток {remaining}";
        SetYmActiveImage(product.Str("image_url"));
        if (string.IsNullOrWhiteSpace(FboYmQtyBox.Text) && remaining > 0)
            FboYmQtyBox.Text = remaining.ToString(CultureInfo.InvariantCulture);
    }

    private void SetYmActiveImage(string url)
    {
        var raw = (url ?? "").Trim();
        _ymActiveImageUrl = raw;
        FboYmActiveImage.Source = null;
        if (raw.Length == 0) return;
        _photos.Load(raw, 110, img =>
        {
            if (_ymActiveImageUrl == raw) FboYmActiveImage.Source = img;
        });
    }

    private void RenderYmRemaining()
    {
        var groups = _ymJob?.Arr("remaining_groups") ?? [];
        _ymRemainingRows.Clear();
        for (var i = 0; i < groups.Count; i++)
        {
            var g = groups[i];
            var row = new RemainingRow
            {
                Index = i,
                Sku = g.Str("sku"),
                Name = g.Str("name", g.Str("product_name")),
                Qty = g.Int("remaining_qty", g.Int("remaining", g.Int("quantity"))).ToString(CultureInfo.InvariantCulture),
                Barcode = g.Str("barcode"),
            };
            _ymRemainingRows.Add(row);
            _photos.Load(g.Str("image_url"), 34, img => row.Photo = img);
        }
    }

    private void RenderYmCargos()
    {
        var cargoes = _ymJob?.Arr("cargoes") ?? [];
        _ymCargoRows.Clear();
        foreach (var cargo in cargoes)
        {
            var items = cargo.Arr("items");
            var contents = items.Count == 0
                ? "пусто"
                : string.Join(", ", items.Select(item => $"{item.Str("sku")} × {item.Int("quantity")}"));
            _ymCargoRows.Add(new RemainingRow
            {
                Sku = cargo.Str("cargo_code"),
                Name = contents,
                Qty = cargo.Int("assigned_qty").ToString(CultureInfo.InvariantCulture),
            });
        }
    }

    private async void OnYmRemainingPick(object sender, RoutedEventArgs e) => await PickYmRemainingAsync();

    private async void OnYmRemainingPick(object sender, MouseButtonEventArgs e) => await PickYmRemainingAsync();

    private async Task PickYmRemainingAsync()
    {
        if (_ymJob is null) return;
        if (FboYmRemainingGrid.SelectedItem is not RemainingRow row) return;
        var groups = _ymJob.Arr("remaining_groups");
        if (row.Index < 0 || row.Index >= groups.Count) return;
        _ymProduct = groups[row.Index];
        RenderYmProduct();
        FocusYmQty();
        await Task.CompletedTask;
    }

    private void OnYmScanKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        OnYmScan(sender, e);
    }

    private void OnYmQtyKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        FocusYmScan();
    }

    private async void OnYmScan(object sender, RoutedEventArgs e)
    {
        var code = (FboYmScanBox.Text ?? "").Trim();
        FboYmScanBox.Clear();
        if (code.Length == 0 || _ymJob is null) return;
        await HandleYmScanAsync(code);
    }

    private async Task HandleYmScanAsync(string code)
    {
        if (_ymJob is null) return;
        if (_ymProduct is null)
        {
            await YmRunAsync(async () =>
            {
                var payload = await _client.YandexFboResolveAsync(_ymJob.Int("id"), code);
                _ymJob = payload.Obj("job") ?? _ymJob;
                if (payload.Str("kind") == "cargo")
                    throw new ApiException("Сначала отсканируйте товар, затем грузоместо");
                var product = payload.Obj("product");
                if (product is null)
                    throw new ApiException("Товар не найден");
                _ymProduct = product;
                var remaining = product.Int("remaining_qty", product.Int("remaining"));
                FboYmQtyBox.Text = remaining > 0
                    ? remaining.ToString(CultureInfo.InvariantCulture)
                    : "";
                RenderYmJob();
                ScanSounds.Ok();
                FocusYmQty();
            }, "Поиск товара...");
            return;
        }

        if (!int.TryParse(FboYmQtyBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var qty) || qty <= 0)
        {
            ScanSounds.Error();
            MessageBox.Show(this, "Укажите количество больше нуля", "FBO YM", MessageBoxButton.OK, MessageBoxImage.Warning);
            FocusYmQty();
            return;
        }

        var productId = _ymProduct.Int("product_id", _ymProduct.Int("id"));
        await YmRunAsync(async () =>
        {
            var payload = await _client.YandexFboAssignAsync(_ymJob.Int("id"), productId, qty, code);
            _ymJob = payload.Obj("job") ?? _ymJob;
            _ymProduct = null;
            FboYmQtyBox.Clear();
            RenderYmJob();
            ScanSounds.Ok();
            SetStatus($"Назначено {qty} шт. в {code}");
            FocusYmScan();
        }, "Назначение в грузоместо...");
    }

    private async void OnYmPrintCargos(object sender, RoutedEventArgs e)
    {
        if (_ymJob is null)
        {
            MessageBox.Show(this, "Сначала откройте задание YM", "FBO YM");
            return;
        }
        await YmRunAsync(async () =>
        {
            var pdf = await _client.YandexFboLabelsPdfAsync(_ymJob.Int("id"));
            await Task.Run(() => GdiPrinter.PrintPdf(
                pdf, _config.LabelProfile(), copies: 2, rotatePortrait: true));
            SetStatus("Ярлыки грузомест отправлены на печать (2 копии)");
            FocusYmScan();
        }, "Печать грузомест...");
    }

    private async Task YmRunAsync(Func<Task> work, string status)
    {
        if (_ymBusy)
        {
            SetStatus("Предыдущая операция ещё выполняется");
            return;
        }
        _ymBusy = true;
        SetStatus(status);
        try { await work(); }
        catch (Exception ex) { ShowYmError(ex); }
        finally { _ymBusy = false; }
    }

    private void ShowYmError(Exception ex)
    {
        if (ex is OperationCanceledException) return;
        ScanSounds.Error();
        if (ex is AuthException) { ShowError(ex); return; }
        if (ex is ApiException)
        {
            MessageBox.Show(this, ex.Message, "FBO YM", MessageBoxButton.OK, MessageBoxImage.Warning);
            SetStatus(ex.Message);
            if (_ymProduct is not null) FocusYmQty();
            else FocusYmScan();
            return;
        }
        ShowError(ex);
        FocusYmScan();
    }
}
