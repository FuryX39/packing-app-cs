using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WarehousePacking.Models;
using WarehousePacking.Services;

namespace WarehousePacking.Views;

public partial class MainWindow
{
    private bool RequireFboNewApi()
    {
        if (_client.ApiOk)
        {
            FboNewApiHint.Text = $"API: {_client.ApiUrl}";
            FboNewApiHint.Foreground = Brushes.DarkGreen;
            return true;
        }
        FboNewApiHint.Text = string.IsNullOrWhiteSpace(_client.ApiError)
            ? "Нет сессии API — нужен run_api.py"
            : _client.ApiError;
        FboNewApiHint.Foreground = Brushes.Firebrick;
        return false;
    }

    private void FocusFboNewScan() => FboNewScanBox.Focus();

    private async void OnReloadFboNew(object sender, RoutedEventArgs e) => await LoadFboNewJobsAsync();

    private async Task LoadFboNewJobsAsync()
    {
        if (!RequireFboNewApi())
        {
            MessageBox.Show(this, string.IsNullOrWhiteSpace(_client.ApiError)
                ? "Запустите python run_api.py (порт 8766) и укажите адрес API в настройках."
                : _client.ApiError, "API упаковщиков");
            return;
        }
        SetStatus("Загрузка заданий FBO WB new...");
        try
        {
            _fboNewJobs = await _client.FboSheetMyJobsAsync();
            var selected = _fboNewPendingJobId > 0 ? _fboNewPendingJobId : _fboNewJob?.IntOrNull("id");
            if (selected is int sid)
            {
                var idx = _fboNewJobs.FindIndex(j => j.Int("id") == sid);
                if (idx >= 0) _fboNewJobsPage = idx / Paging.PageSize;
            }
            RenderFboNewJobs();
            SetStatus($"FBO WB new заданий: {_fboNewJobs.Count}");
            FocusFboNewScan();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void RenderFboNewJobs()
    {
        var (visible, page) = Paging.Slice(_fboNewJobs, _fboNewJobsPage);
        _fboNewJobsPage = page;
        _fboNewJobsFilling = true;
        _fboNewJobRows.Clear();
        foreach (var j in visible)
        {
            _fboNewJobRows.Add(new FbsJobRow
            {
                Id = j.Int("id"),
                Status = Paging.JobStatusRu(j.Str("status")),
                Progress = $"{j.Int("box_assigned")}/{j.Int("box_total")}",
            });
        }
        var selected = _fboNewPendingJobId > 0 ? _fboNewPendingJobId : _fboNewJob?.IntOrNull("id");
        if (selected is int sid)
            FboNewJobsGrid.SelectedItem = _fboNewJobRows.FirstOrDefault(x => x.Id == sid);
        _fboNewJobsFilling = false;
        FboNewJobsPageLabel.Text = Paging.RangeLabel(_fboNewJobs.Count, _fboNewJobsPage);
        FboNewJobsPrev.IsEnabled = _fboNewJobsPage > 0;
        FboNewJobsNext.IsEnabled = _fboNewJobsPage + 1 < Paging.PageCount(_fboNewJobs.Count);
    }

    private void OnFboNewJobsPrev(object sender, RoutedEventArgs e)
    {
        if (_fboNewJobsPage > 0) { _fboNewJobsPage--; RenderFboNewJobs(); }
    }

    private void OnFboNewJobsNext(object sender, RoutedEventArgs e)
    {
        if (_fboNewJobsPage + 1 < Paging.PageCount(_fboNewJobs.Count)) { _fboNewJobsPage++; RenderFboNewJobs(); }
    }

    private void OnFboNewJobSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_fboNewJobsFilling || FboNewJobsGrid.SelectedItem is not FbsJobRow row) return;
        _fboNewPendingJobId = row.Id;
        _fboNewSelectTimer.Stop();
        _fboNewSelectTimer.Start();
    }

    private async Task OpenFboNewJobAsync(int jobId)
    {
        var previous = _fboNewOpenCts;
        var cts = new CancellationTokenSource();
        _fboNewOpenCts = cts;
        previous?.Cancel();
        SetStatus($"Открытие задания #{jobId}...");
        try
        {
            _fboNewQtyWarning = "";
            ApplyFboNewQtyWarning();
            var job = await _client.FboSheetOpenJobAsync(jobId, cts.Token);
            if (!ReferenceEquals(_fboNewOpenCts, cts)) return;
            _fboNewJob = job;
            RenderFboNewJob();
            FocusFboNewScan();
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_fboNewOpenCts, cts)) ShowFboNewError(ex);
        }
        finally
        {
            if (ReferenceEquals(_fboNewOpenCts, cts))
            {
                _fboNewOpenCts = null;
                cts.Dispose();
            }
        }
    }

    private void RenderFboNewJob()
    {
        var job = _fboNewJob;
        var title = job is null ? "Выберите задание" : $"FBO WB new #{job.Str("id")}";
        if (job is not null && job.Str("supply_id").Length > 0)
            title += $" · поставка {job.Str("supply_id")}";
        FboNewJobTitle.Text = title;
        var assigned = job?.Int("box_assigned") ?? 0;
        var total = job?.Int("box_total") ?? 0;
        var printed = job?.Int("box_printed") ?? 0;
        var pending = job?.Int("box_pending") ?? 0;
        var pcs = job is null ? "" : $" · шт. {job.Int("pcs_assigned")}/{job.Int("pcs_plan")}";
        FboNewJobStats.Text = $"короба {assigned}/{total} · напечатано {printed} · не печатались {pending}{pcs}";
        RefreshFboNewSelectedText();
        RenderFboNewRemaining();
        ApplyFboNewQtyWarning();
    }

    private void RenderFboNewRemaining()
    {
        var groups = _fboNewJob?.Arr("remaining_groups") ?? [];
        _fboNewRemainingRows.Clear();
        for (var i = 0; i < groups.Count; i++)
        {
            var g = groups[i];
            var row = new RemainingRow
            {
                Index = i,
                Sku = g.Str("sku"),
                Name = g.Str("name", g.Str("sku")),
                Qty = g.Str("quantity"),
                Barcode = g.Str("barcode"),
            };
            _fboNewRemainingRows.Add(row);
            _photos.Load(g.Str("image_url"), 34, img => row.Photo = img);
        }
    }

    private void OnFboNewRemainingDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FboNewRemainingGrid.SelectedItem is not RemainingRow row) return;
        var groups = _fboNewJob?.Arr("remaining_groups") ?? [];
        if (row.Index < 0 || row.Index >= groups.Count) return;
        ApplyFboNewProduct(groups[row.Index], null);
    }

    private void RefreshFboNewSelectedText()
    {
        if (_fboNewProduct is null)
        {
            FboNewSelectedText.Text = "Сначала пикните товар, затем ШК короба WB";
            return;
        }
        var sku = _fboNewProduct.Str("sku");
        var name = _fboNewProduct.Str("name", sku);
        var left = _fboNewProduct.Int("quantity", _fboNewProduct.Int("qty_plan") - _fboNewProduct.Int("qty_assigned"));
        FboNewSelectedText.Text = $"Товар {sku} · {name} · баркод {_fboNewProduct.Str("barcode")} · осталось {left} шт. Пикните ШК короба WB.";
    }

    private void ApplyFboNewQtyWarning()
    {
        var text = (_fboNewQtyWarning ?? "").Trim();
        FboNewQtyWarning.Text = text;
        FboNewQtyWarning.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowFboNewQtyWarningDialog(string warning)
    {
        var text = (warning ?? "").Trim();
        _fboNewQtyWarning = text;
        ApplyFboNewQtyWarning();
        if (text.Length == 0) return;
        MessageBox.Show(this, text, "Нестандартный короб", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void ApplyFboNewProduct(JsonMap product, int? suggestedQty)
    {
        _fboNewProduct = product;
        _fboNewQtyWarning = "";
        ApplyFboNewQtyWarning();
        var barcode = product.Str("barcode");
        var sku = product.Str("sku");
        var qtyText = "";
        if (FboNewRememberQty.IsChecked == true &&
            (_fboNewRememberedQty.TryGetValue(barcode, out var remembered) ||
             (sku.Length > 0 && _fboNewRememberedQty.TryGetValue(sku, out remembered))))
        {
            qtyText = remembered.ToString();
        }
        else if (suggestedQty is int s && s > 0)
        {
            qtyText = s.ToString();
        }
        FboNewQtyBox.Text = qtyText;
        RefreshFboNewSelectedText();
        if (string.IsNullOrWhiteSpace(FboNewQtyBox.Text))
            FboNewQtyBox.Focus();
        else
            FocusFboNewScan();
    }

    private void ClearFboNewProduct()
    {
        _fboNewProduct = null;
        FboNewQtyBox.Text = "";
        RefreshFboNewSelectedText();
    }

    private int? ReadFboNewQty()
    {
        var raw = (FboNewQtyBox.Text ?? "").Trim();
        if (raw.Length == 0) return null;
        return int.TryParse(raw, out var n) ? n : null;
    }

    private async void OnFboNewPrintBoxes(object sender, RoutedEventArgs e)
    {
        if (_fboNewJob is null)
        {
            MessageBox.Show(this, "Сначала откройте задание", "FBO WB new");
            return;
        }
        if (!int.TryParse((FboNewPrintCount.Text ?? "").Trim(), out var count) || count <= 0)
        {
            MessageBox.Show(this, "Укажите, сколько ШК коробов напечатать", "FBO WB new");
            return;
        }
        var jobId = _fboNewJob.Int("id");
        await FboNewRunAsync(async () =>
        {
            var payload = await _client.FboSheetPrintBoxesAsync(jobId, count);
            _fboNewJob = payload.Obj("job") ?? _fboNewJob;
            var boxes = payload.Arr("boxes");
            if (boxes.Count > 0)
                _fboNewLastPrintedBoxId = boxes[^1].Int("id");
            var pdfs = DecodeFboNewPdfs(payload);
            if (pdfs.Count > 0)
                await Task.Run(() => GdiPrinter.PrintPdfs(pdfs, _config.LabelProfile()));
            RenderFboNewJob();
            RenderFboNewJobs();
            SetStatus($"Напечатано ШК коробов: {pdfs.Count}");
            FocusFboNewScan();
        }, "Печать ШК коробов...");
    }

    private async void OnFboNewReprintLast(object sender, RoutedEventArgs e)
    {
        if (_fboNewJob is null || _fboNewLastPrintedBoxId <= 0)
        {
            MessageBox.Show(this, "Нет последнего напечатанного ШК короба", "FBO WB new");
            return;
        }
        var jobId = _fboNewJob.Int("id");
        var boxId = _fboNewLastPrintedBoxId;
        await FboNewRunAsync(async () =>
        {
            var payload = await _client.FboSheetReprintBoxAsync(jobId, boxId);
            _fboNewJob = payload.Obj("job") ?? _fboNewJob;
            var pdfs = DecodeFboNewPdfs(payload);
            if (pdfs.Count > 0)
                await Task.Run(() => GdiPrinter.PrintPdfs(pdfs, _config.LabelProfile()));
            RenderFboNewJob();
            SetStatus("Ярлык короба перепечатан");
            FocusFboNewScan();
        }, "Перепечатка...");
    }

    private static List<byte[]> DecodeFboNewPdfs(JsonMap payload)
    {
        var encoded = payload.StrList("pdfs_base64");
        if (encoded.Count == 0 && payload.Str("pdf_base64").Length > 0)
            encoded = [payload.Str("pdf_base64")];
        return encoded.Where(s => s.Length > 0).Select(Convert.FromBase64String).ToList();
    }

    private void OnFboNewScanKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        _ = HandleFboNewScanAsync();
    }

    private async void OnFboNewScan(object sender, RoutedEventArgs e) => await HandleFboNewScanAsync();

    private async Task HandleFboNewScanAsync()
    {
        if (_fboNewJob is null)
        {
            MessageBox.Show(this, "Сначала откройте задание", "FBO WB new");
            return;
        }
        var code = (FboNewScanBox.Text ?? "").Trim();
        FboNewScanBox.Text = "";
        if (code.Length == 0) return;
        var jobId = _fboNewJob.Int("id");
        await FboNewRunAsync(async () =>
        {
            var resolved = await _client.FboSheetResolveAsync(jobId, code);
            var kind = resolved.Str("kind");
            if (kind == "product")
            {
                var product = resolved.Obj("product");
                if (product is null)
                    throw new ApiException("Товар не распознан");
                ApplyFboNewProduct(product, resolved.IntOrNull("suggested_qty"));
                SetStatus($"Товар {product.Str("sku")}");
                return;
            }
            if (kind == "wb_box")
            {
                if (_fboNewProduct is null)
                    throw new ApiException("Сначала пикните товар, затем ШК короба WB");
                var qty = ReadFboNewQty();
                if (qty is null or <= 0)
                    throw new ApiException("Укажите количество товара в коробе");
                var productBarcode = _fboNewProduct.Str("barcode");
                var payload = await _client.FboSheetAssignAsync(jobId, code, productBarcode, qty.Value);
                _fboNewJob = payload.Obj("job") ?? _fboNewJob;
                if (FboNewRememberQty.IsChecked == true)
                {
                    _fboNewRememberedQty[productBarcode] = qty.Value;
                    var sku = _fboNewProduct.Str("sku");
                    if (sku.Length > 0)
                        _fboNewRememberedQty[sku] = qty.Value;
                }
                var box = payload.Obj("box");
                var warning = payload.Str("qty_warning");
                var remaining = (_fboNewJob?.Arr("remaining_groups") ?? [])
                    .FirstOrDefault(g => g.Str("barcode").Equals(productBarcode, StringComparison.OrdinalIgnoreCase));
                if (remaining is null)
                    ClearFboNewProduct();
                else
                {
                    _fboNewProduct = remaining;
                    RefreshFboNewSelectedText();
                }
                RenderFboNewJob();
                RenderFboNewJobs();
                ShowFboNewQtyWarningDialog(warning);
                SetStatus($"Короб {box?.Str("box_id") ?? ""} · {qty} шт.");
                FocusFboNewScan();
                return;
            }
            throw new ApiException("Штрихкод не распознан");
        }, "Сканирование...");
    }

    private async Task FboNewRunAsync(Func<Task> work, string status)
    {
        if (_fboNewBusy)
        {
            SetStatus("Предыдущая операция ещё выполняется");
            return;
        }
        _fboNewBusy = true;
        SetStatus(status);
        try { await work(); }
        catch (Exception ex) { ShowFboNewError(ex); }
        finally { _fboNewBusy = false; }
    }

    private void ShowFboNewError(Exception ex)
    {
        if (ex is OperationCanceledException) return;
        if (ex is AuthException) { ShowError(ex); return; }
        if (ex is ApiException)
        {
            MessageBox.Show(this, ex.Message, "FBO WB new", MessageBoxButton.OK, MessageBoxImage.Warning);
            SetStatus(ex.Message);
            FocusFboNewScan();
            return;
        }
        ShowError(ex);
        FocusFboNewScan();
    }
}
