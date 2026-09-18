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
            _fboNewClosingPallet = false;
            if (_fboNewJob?.IntOrNull("id") != jobId)
            {
                _fboNewLastPrintedBoxId = 0;
                _fboNewLastPrintedPalletId = 0;
                ClearFboNewProduct();
            }
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
        var pallets = job is null ? "" : $" · паллеты {job.Int("pallet_closed")}/{job.Int("pallet_total")}";
        var pcs = job is null ? "" : $" · шт. {job.Int("pcs_assigned")}/{job.Int("pcs_plan")}";
        FboNewJobStats.Text = $"грузоместа {assigned}/{total} · напечатано {printed} · не печатались {pending}{pallets}{pcs}";
        RefreshFboNewSelectedText();
        RenderFboNewRemaining();
        RenderFboNewOverview();
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
        if (_fboNewJob?.Obj("open_pallet") is null)
        {
            MessageBox.Show(this, "Сначала пикните паллет", "FBO WB new");
            return;
        }
        var groups = _fboNewJob?.Arr("remaining_groups") ?? [];
        if (row.Index < 0 || row.Index >= groups.Count) return;
        ApplyFboNewProduct(groups[row.Index], null);
    }

    private void OnFboNewManualToggle(object sender, RoutedEventArgs e)
    {
        FboNewManualPanel.Visibility = FboNewManualBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (FboNewManualBox.IsChecked == true)
            RenderFboNewRemaining();
        FocusFboNewScan();
    }

    private void RefreshFboNewPalletText()
    {
        var open = _fboNewJob?.Obj("open_pallet");
        if (_fboNewClosingPallet && open is not null)
        {
            FboNewPalletText.Text = $"Закрытие паллета {open.Str("pallet_id")} — пикните его ШК";
            FboNewClosePalletBtn.Content = "Отмена";
            return;
        }
        FboNewClosePalletBtn.Content = "Закрыть паллет";
        if (open is null)
        {
            FboNewPalletText.Text = "Сначала пикните паллет";
            return;
        }
        var boxes = open.Int("box_count");
        FboNewPalletText.Text = $"Паллет {open.Str("pallet_id")} · грузомест {boxes}";
    }

    private void RefreshFboNewSelectedText()
    {
        RefreshFboNewPalletText();
        if (_fboNewClosingPallet)
        {
            FboNewSelectedText.Text = "Пикните ШК открытого паллета, чтобы закрыть его";
            return;
        }
        if (_fboNewJob?.Obj("open_pallet") is null)
        {
            FboNewSelectedText.Text = "Пикните паллет";
            return;
        }
        if (_fboNewProduct is null)
        {
            FboNewSelectedText.Text = "Пикните товар, затем грузоместо";
            return;
        }
        var sku = _fboNewProduct.Str("sku");
        var name = _fboNewProduct.Str("name", sku);
        var left = _fboNewProduct.Int("quantity", _fboNewProduct.Int("qty_plan") - _fboNewProduct.Int("qty_assigned"));
        FboNewSelectedText.Text = $"Товар {sku} · {name} · баркод {_fboNewProduct.Str("barcode")} · осталось {left} шт. Пикните грузоместо.";
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
        MessageBox.Show(this, text, "Нестандартное грузоместо", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void ApplyFboNewProduct(JsonMap product, int? suggestedQty)
    {
        _fboNewProduct = product;
        FboNewQtyPanel.Visibility = Visibility.Visible;
        _fboNewQtyWarning = "";
        ApplyFboNewQtyWarning();
        if (string.IsNullOrWhiteSpace(FboNewQtyBox.Text))
        {
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
        }
        ApplyFboNewProductionDate(product);
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
        FboNewProductionDate.SelectedDate = null;
        FboNewQtyPanel.Visibility = Visibility.Collapsed;
        RefreshFboNewSelectedText();
    }

    private int? ReadFboNewQty()
    {
        var raw = (FboNewQtyBox.Text ?? "").Trim();
        if (raw.Length == 0) return null;
        return int.TryParse(raw, out var n) ? n : null;
    }

    private void ApplyFboNewProductionDate(JsonMap? product)
    {
        FboNewProductionDate.SelectedDate = RememberedProductionDate(product);
    }

    private DateTime? RememberedProductionDate(JsonMap? product)
    {
        if (product is null)
            return null;
        var barcode = product.Str("barcode");
        var sku = product.Str("sku");
        if (barcode.Length > 0 && _fboNewProductionDates.TryGetValue(barcode, out var remembered))
            return remembered;
        if (sku.Length > 0 && _fboNewProductionDates.TryGetValue(sku, out remembered))
            return remembered;
        return null;
    }

    private string? ReadFboNewProductionDate()
    {
        return FboNewProductionDate.SelectedDate?.ToString("yyyy-MM-dd");
    }

    private string? ProductionDateForAssign()
    {
        if (_fboNewProduct is null || !_fboNewProduct.Flag("has_shelf_life"))
            return null;
        var picked = ReadFboNewProductionDate();
        if (!string.IsNullOrWhiteSpace(picked))
            return picked;
        return RememberedProductionDate(_fboNewProduct)?.ToString("yyyy-MM-dd");
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
            MessageBox.Show(this, "Укажите, сколько ШК грузомест напечатать", "FBO WB new");
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
            SetStatus($"Напечатано ШК грузомест: {pdfs.Count}");
            FocusFboNewScan();
        }, "Печать ШК грузомест...");
    }

    private async void OnFboNewReprintLast(object sender, RoutedEventArgs e)
    {
        if (_fboNewJob is null || _fboNewLastPrintedBoxId <= 0)
        {
            MessageBox.Show(this, "Нет последнего напечатанного ШК грузоместа", "FBO WB new");
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
            SetStatus("Ярлык грузоместа перепечатан");
            FocusFboNewScan();
        }, "Перепечатка...");
    }

    private async void OnFboNewPrintPallets(object sender, RoutedEventArgs e)
    {
        if (_fboNewJob is null)
        {
            MessageBox.Show(this, "Сначала откройте задание", "FBO WB new");
            return;
        }
        if (!int.TryParse((FboNewPalletPrintCount.Text ?? "").Trim(), out var count) || count <= 0)
        {
            MessageBox.Show(this, "Укажите, сколько ШК паллет напечатать", "FBO WB new");
            return;
        }
        var jobId = _fboNewJob.Int("id");
        await FboNewRunAsync(async () =>
        {
            var payload = await _client.FboSheetPrintPalletsAsync(jobId, count);
            _fboNewJob = payload.Obj("job") ?? _fboNewJob;
            var pallets = payload.Arr("pallets");
            if (pallets.Count > 0)
                _fboNewLastPrintedPalletId = pallets[^1].Int("id");
            var pdfs = DecodeFboNewPdfs(payload);
            if (pdfs.Count > 0)
                await Task.Run(() => GdiPrinter.PrintPdfs(pdfs, _config.LabelProfile()));
            RenderFboNewJob();
            RenderFboNewJobs();
            SetStatus($"Напечатано ШК паллет: {pdfs.Count}");
            FocusFboNewScan();
        }, "Печать ШК паллет...");
    }

    private async void OnFboNewReprintLastPallet(object sender, RoutedEventArgs e)
    {
        if (_fboNewJob is null || _fboNewLastPrintedPalletId <= 0)
        {
            MessageBox.Show(this, "Нет последнего напечатанного ШК паллета", "FBO WB new");
            return;
        }
        var jobId = _fboNewJob.Int("id");
        var palletId = _fboNewLastPrintedPalletId;
        await FboNewRunAsync(async () =>
        {
            var payload = await _client.FboSheetReprintPalletAsync(jobId, palletId);
            _fboNewJob = payload.Obj("job") ?? _fboNewJob;
            var pdfs = DecodeFboNewPdfs(payload);
            if (pdfs.Count > 0)
                await Task.Run(() => GdiPrinter.PrintPdfs(pdfs, _config.LabelProfile()));
            RenderFboNewJob();
            SetStatus("Ярлык паллета перепечатан");
            FocusFboNewScan();
        }, "Перепечатка паллета...");
    }

    private void OnFboNewClosePallet(object sender, RoutedEventArgs e)
    {
        if (_fboNewJob?.Obj("open_pallet") is null)
        {
            MessageBox.Show(this, "Сначала пикните паллет", "FBO WB new");
            return;
        }
        _fboNewClosingPallet = !_fboNewClosingPallet;
        RefreshFboNewSelectedText();
        FocusFboNewScan();
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
            if (_fboNewClosingPallet)
            {
                var closed = await _client.FboSheetClosePalletAsync(jobId, code);
                _fboNewJob = closed.Obj("job") ?? await _client.FboSheetOpenJobAsync(jobId);
                _fboNewClosingPallet = false;
                RenderFboNewJob();
                RenderFboNewJobs();
                SetStatus($"Паллет {closed.Obj("pallet")?.Str("pallet_id") ?? ""} закрыт");
                FocusFboNewScan();
                return;
            }
            var resolved = await _client.FboSheetResolveAsync(jobId, code);
            var kind = resolved.Str("kind");
            if (kind == "pallet")
            {
                _fboNewJob = resolved.Obj("job") ?? await _client.FboSheetOpenJobAsync(jobId);
                RenderFboNewJob();
                SetStatus($"Паллет {resolved.Obj("pallet")?.Str("pallet_id") ?? ""}");
                FocusFboNewScan();
                return;
            }
            if (kind == "product")
            {
                if (_fboNewJob?.Obj("open_pallet") is null)
                    throw new ApiException("Сначала пикните паллет");
                var product = resolved.Obj("product");
                if (product is null)
                    throw new ApiException("Товар не распознан");
                ApplyFboNewProduct(product, resolved.IntOrNull("suggested_qty"));
                SetStatus($"Товар {product.Str("sku")}");
                return;
            }
            if (kind == "wb_box")
            {
                if (_fboNewJob?.Obj("open_pallet") is null)
                    throw new ApiException("Сначала пикните паллет");
                if (_fboNewProduct is null)
                    throw new ApiException("Сначала пикните товар, затем грузоместо");
                var qty = ReadFboNewQty();
                if (qty is null or <= 0)
                    throw new ApiException("Укажите количество товара в грузоместе");
                var productBarcode = _fboNewProduct.Str("barcode");
                var productionDate = ProductionDateForAssign();
                var payload = await _client.FboSheetAssignAsync(jobId, code, productBarcode, qty.Value, productionDate);
                _fboNewJob = payload.Obj("job") ?? _fboNewJob;
                if (FboNewProductionDate.SelectedDate is DateTime produced)
                {
                    _fboNewProductionDates[productBarcode] = produced;
                    var producedSku = _fboNewProduct.Str("sku");
                    if (producedSku.Length > 0)
                        _fboNewProductionDates[producedSku] = produced;
                }
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
                    ApplyFboNewProductionDate(remaining);
                    if (FboNewRememberQty.IsChecked != true)
                        FboNewQtyBox.Text = "";
                    RefreshFboNewSelectedText();
                }
                RenderFboNewJob();
                RenderFboNewJobs();
                ShowFboNewQtyWarningDialog(warning);
                SetStatus($"Грузоместо {box?.Str("box_id") ?? ""} · {qty} шт.");
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

    private void OnFboNewOverviewToggle(object sender, RoutedEventArgs e)
    {
        _fboNewOverviewOpen = !_fboNewOverviewOpen;
        FboNewOverviewCol.Width = new GridLength(_fboNewOverviewOpen ? 380 : 0);
        FboNewOverviewPanel.Visibility = _fboNewOverviewOpen ? Visibility.Visible : Visibility.Collapsed;
        FboNewOverviewHeaderBtn.Content = _fboNewOverviewOpen ? "Скрыть грузоместа" : "Грузоместа";
        if (_fboNewOverviewOpen)
            RenderFboNewOverview();
    }

    private void OnFboNewUnassignContextOpening(object sender, ContextMenuEventArgs e)
    {
        if (!CanUnassignOverview((sender as FrameworkElement)?.DataContext))
            e.Handled = true;
    }

    private static bool CanUnassignOverview(object? data) =>
        (data is FboOverviewLineRow line && line.CanUnassign)
        || (data is FboOverviewGroupRow group && group.CanUnassign);

    private async void OnFboNewUnassignClick(object sender, RoutedEventArgs e)
    {
        var data = ((sender as MenuItem)?.Parent as ContextMenu)?.PlacementTarget is FrameworkElement target
            ? target.DataContext
            : (sender as FrameworkElement)?.DataContext;
        int boxId;
        string boxCode;
        string productBarcode;
        if (data is FboOverviewLineRow line && line.CanUnassign)
        {
            boxId = line.BoxId;
            boxCode = line.BoxCode;
            productBarcode = line.ProductBarcode;
        }
        else if (data is FboOverviewGroupRow group && group.CanUnassign)
        {
            boxId = group.BoxId;
            boxCode = group.BoxCode;
            productBarcode = group.ProductBarcode;
        }
        else
            return;
        if (_fboNewJob is null || boxId <= 0)
            return;
        var jobId = _fboNewJob.Int("id");
        var confirm = string.IsNullOrWhiteSpace(productBarcode)
            ? $"Снять все товары с грузоместа {boxCode}?"
            : $"Снять привязку товара к грузоместу {boxCode}?";
        if (MessageBox.Show(this, confirm, "FBO WB new", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        await FboNewRunAsync(async () =>
        {
            var payload = await _client.FboSheetUnassignAsync(jobId, boxId, productBarcode);
            _fboNewJob = payload.Obj("job") ?? _fboNewJob;
            SyncFboNewSelectedProduct();
            RenderFboNewJob();
            RenderFboNewJobs();
            SetStatus($"Снята привязка с грузоместа {boxCode}");
            FocusFboNewScan();
        }, "Снятие привязки...");
    }

    private void SyncFboNewSelectedProduct()
    {
        if (_fboNewProduct is null || _fboNewJob is null)
            return;
        var barcode = _fboNewProduct.Str("barcode");
        if (barcode.Length == 0)
            return;
        var remaining = _fboNewJob.Arr("remaining_groups")
            .FirstOrDefault(item => item.Str("barcode").Equals(barcode, StringComparison.OrdinalIgnoreCase));
        if (remaining is not null)
        {
            _fboNewProduct = remaining;
            return;
        }
        var product = _fboNewJob.Arr("products")
            .FirstOrDefault(item => item.Str("barcode").Equals(barcode, StringComparison.OrdinalIgnoreCase));
        if (product is not null)
            _fboNewProduct = product;
    }

    private void RenderFboNewOverview()
    {
        _fboNewByProductRows.Clear();
        _fboNewByCargoRows.Clear();
        var job = _fboNewJob;
        if (job is null) return;

        var products = job.Arr("products");
        var boxes = job.Arr("boxes");
        foreach (var product in products)
        {
            var barcode = product.Str("barcode");
            var sku = product.Str("sku");
            var name = product.Str("name", sku);
            var title = sku.Length > 0 ? sku : barcode;
            if (name.Length > 0 && name != title)
                title += $" · {name}";
            var lines = new List<FboOverviewLineRow>();
            var dates = new List<string>();
            foreach (var box in boxes)
            {
                foreach (var line in CargoLinesForBarcode(box, barcode))
                    lines.Add(line);
                foreach (var date in ItemDatesForBarcode(box, barcode))
                {
                    if (!dates.Exists(x => x.Equals(date, StringComparison.OrdinalIgnoreCase)))
                        dates.Add(date);
                }
            }
            title = AppendDates(title, dates);
            _fboNewByProductRows.Add(new FboOverviewGroupRow
            {
                Title = title,
                Lines = lines,
                EmptyText = "Пусто",
            });
        }

        var filled = new List<JsonMap>();
        var empty = new List<JsonMap>();
        foreach (var box in boxes)
        {
            if (BoxHasItems(box)) filled.Add(box);
            else empty.Add(box);
        }
        foreach (var box in filled.Concat(empty))
        {
            _fboNewByCargoRows.Add(new FboOverviewGroupRow
            {
                Title = BoxTitle(box),
                Lines = CargoProductLines(box),
                EmptyText = "Пусто",
                BoxId = box.Int("id"),
                BoxCode = box.Str("box_id"),
                CanUnassign = BoxHasItems(box),
            });
        }

        _fboNewByPalletRows.Clear();
        foreach (var pallet in job.Arr("pallets"))
        {
            var palletId = pallet.Int("id");
            var human = pallet.Str("pallet_id");
            var lines = new List<FboOverviewLineRow>();
            foreach (var box in boxes)
            {
                var onPallet = box.Int("pallet_id") == palletId ||
                    (human.Length > 0 && box.Str("pallet_human_id").Equals(human, StringComparison.OrdinalIgnoreCase));
                if (!onPallet) continue;
                var productLines = CargoProductLines(box);
                if (productLines.Count == 0)
                    lines.Add(OverviewLine(BoxTitle(box, includePallet: false), box, "", canUnassign: false));
                else
                    foreach (var line in productLines)
                    {
                        lines.Add(OverviewLine(
                            $"{BoxTitle(box, includePallet: false)} · {line.Text}",
                            box,
                            line.ProductBarcode,
                            canUnassign: line.CanUnassign));
                    }
            }
            _fboNewByPalletRows.Add(new FboOverviewGroupRow
            {
                Title = PalletTitle(pallet),
                Lines = lines,
                EmptyText = "Нет грузомест",
            });
        }
    }

    private static string PalletTitle(JsonMap pallet)
    {
        var id = pallet.Str("pallet_id");
        if (id.Length == 0) id = $"№{pallet.Int("seq")}";
        var status = PalletStatusRu(pallet.Str("status"));
        var boxes = pallet.Int("box_count");
        return $"{id} · {status} · грузомест {boxes}";
    }

    private static string PalletStatusRu(string status) => status switch
    {
        "open" => "открыт",
        "closed" => "закрыт",
        "printed" => "напечатан",
        _ => status,
    };

    private static string BoxTitle(JsonMap box, bool includePallet = true)
    {
        var id = box.Str("box_id");
        if (id.Length == 0) id = box.Str("order_display");
        if (id.Length == 0) id = $"№{box.Int("seq")}";
        if (!includePallet) return id;
        var pallet = box.Str("pallet_human_id");
        return pallet.Length > 0 ? $"{id} · {pallet}" : id;
    }

    private static bool BoxHasItems(JsonMap box)
    {
        if (box.Arr("items").Count > 0) return true;
        return box.Str("product_barcode").Length > 0 && box.Int("quantity") > 0;
    }

    private static IEnumerable<JsonMap> ItemsForBarcode(JsonMap box, string barcode)
    {
        if (barcode.Length == 0) yield break;
        var found = false;
        foreach (var item in box.Arr("items"))
        {
            if (!item.Str("product_barcode").Equals(barcode, StringComparison.OrdinalIgnoreCase))
                continue;
            found = true;
            yield return item;
        }
        if (!found &&
            box.Str("product_barcode").Equals(barcode, StringComparison.OrdinalIgnoreCase) &&
            box.Int("quantity") > 0)
        {
            yield return box;
        }
    }

    private static FboOverviewLineRow OverviewLine(string text, JsonMap box, string productBarcode, bool canUnassign)
    {
        return new FboOverviewLineRow
        {
            Text = text,
            BoxId = box.Int("id"),
            BoxCode = box.Str("box_id"),
            ProductBarcode = productBarcode,
            CanUnassign = canUnassign && box.Int("id") > 0,
        };
    }

    private static List<FboOverviewLineRow> CargoLinesForBarcode(JsonMap box, string barcode)
    {
        var lines = new List<FboOverviewLineRow>();
        foreach (var item in ItemsForBarcode(box, barcode))
        {
            var qty = item.Int("quantity", item.Int("item_qty"));
            if (qty <= 0)
                qty = box.Int("quantity");
            var text = AppendDate(
                qty > 0 ? $"{BoxTitle(box)} · {qty} шт." : BoxTitle(box),
                item.Str("expiry"));
            lines.Add(OverviewLine(text, box, barcode, canUnassign: true));
        }
        return lines;
    }

    private static List<string> ItemDatesForBarcode(JsonMap box, string barcode)
    {
        var dates = new List<string>();
        foreach (var item in ItemsForBarcode(box, barcode))
        {
            var date = DisplayDate(item.Str("expiry"));
            if (date.Length > 0 && !dates.Exists(x => x.Equals(date, StringComparison.OrdinalIgnoreCase)))
                dates.Add(date);
        }
        return dates;
    }

    private static List<FboOverviewLineRow> CargoProductLines(JsonMap box)
    {
        var lines = new List<FboOverviewLineRow>();
        foreach (var item in box.Arr("items"))
        {
            var barcode = item.Str("product_barcode");
            lines.Add(OverviewLine(ProductLine(item), box, barcode, canUnassign: barcode.Length > 0));
        }
        if (lines.Count == 0 && box.Str("product_barcode").Length > 0 && box.Int("quantity") > 0)
        {
            var barcode = box.Str("product_barcode");
            lines.Add(OverviewLine(ProductLine(box), box, barcode, canUnassign: true));
        }
        return lines;
    }

    private static string ProductLine(JsonMap item)
    {
        var sku = item.Str("sku");
        var name = item.Str("product_name", sku);
        var label = sku.Length > 0 ? sku : item.Str("product_barcode");
        if (name.Length > 0 && name != label)
            label += $" · {name}";
        var qty = item.Int("quantity", item.Int("item_qty"));
        if (qty > 0)
            label += $" · {qty} шт.";
        return AppendDate(label, item.Str("expiry"));
    }

    private static string AppendDates(string label, IReadOnlyList<string> dates)
    {
        if (dates.Count == 0) return label;
        return $"{label} · {string.Join(", ", dates)}";
    }

    private static string AppendDate(string label, string expiry)
    {
        var date = DisplayDate(expiry);
        return date.Length > 0 ? $"{label} · {date}" : label;
    }

    private static string DisplayDate(string raw)
    {
        var text = (raw ?? "").Trim();
        if (text.Length == 0) return "";
        string[] formats = ["dd.MM.yyyy", "yyyy-MM-dd", "dd/MM/yyyy", "dd-MM-yyyy"];
        if (DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return dt.ToString("dd.MM.yyyy");
        return text;
    }
}
