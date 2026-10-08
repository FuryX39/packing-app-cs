using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WarehousePacking.Models;
using WarehousePacking.Services;

namespace WarehousePacking.Views;

public partial class MainWindow
{
    private DatePickerTextBox? _fboNewProductionDateTextBox;
    private bool _fboNewFormattingProductionDate;
    private bool _fboNewChangingProductionDate;

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
            if (_fboNewJob?.IntOrNull("id") != jobId)
            {
                _fboNewLastPrintedBoxId = 0;
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
        var pcs = job is null ? "" : $" · шт. {job.Int("pcs_assigned")}/{job.Int("pcs_plan")}";
        FboNewJobStats.Text = $"грузоместа {assigned}/{total} · напечатано {printed} · не печатались {pending}{pcs}";
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
        if (FindParent<DataGridRow>(e.OriginalSource as DependencyObject) is null)
            return;
        if (FboNewRemainingGrid.SelectedItem is not RemainingRow row) return;
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

    private void RefreshFboNewSelectedText()
    {
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
        var previousBarcode = _fboNewProduct?.Str("barcode") ?? "";
        var barcode = product.Str("barcode");
        var sku = product.Str("sku");
        var sameProduct = previousBarcode.Length > 0 &&
            previousBarcode.Equals(barcode, StringComparison.OrdinalIgnoreCase);
        _fboNewProduct = product;
        FboNewQtyPanel.Visibility = Visibility.Visible;
        _fboNewQtyWarning = "";
        ApplyFboNewQtyWarning();
        if (!sameProduct)
        {
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
        else if (suggestedQty is int s && s > 0)
        {
            FboNewQtyBox.Text = s.ToString();
        }
        ApplyFboNewProductionDate(product);
        RefreshFboNewSelectedText();
        FocusFboNewProductFields();
    }

    private void ClearFboNewProduct()
    {
        _fboNewProduct = null;
        FboNewQtyBox.Text = "";
        _fboNewChangingProductionDate = true;
        try
        {
            FboNewProductionDate.SelectedDate = null;
            FboNewProductionDate.Text = "";
        }
        finally
        {
            _fboNewChangingProductionDate = false;
        }
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
        _fboNewChangingProductionDate = true;
        try
        {
            FboNewProductionDate.SelectedDate = RememberedProductionDate(product);
        }
        finally
        {
            _fboNewChangingProductionDate = false;
        }
    }

    private void FocusFboNewProductFields()
    {
        if (_fboNewProduct?.Flag("has_shelf_life") == true &&
            FboNewProductionDate.SelectedDate is null)
        {
            FboNewProductionDate.Focus();
            _fboNewProductionDateTextBox?.Focus();
            return;
        }
        FocusFboNewQuantity();
    }

    private void FocusFboNewQuantity()
    {
        FboNewQtyBox.Focus();
        FboNewQtyBox.SelectAll();
    }

    private void OnFboNewProductionDateLoaded(object sender, RoutedEventArgs e)
    {
        var textBox = FboNewProductionDate.Template.FindName(
            "PART_TextBox",
            FboNewProductionDate) as DatePickerTextBox;
        if (ReferenceEquals(textBox, _fboNewProductionDateTextBox))
            return;
        if (_fboNewProductionDateTextBox is not null)
            _fboNewProductionDateTextBox.TextChanged -= OnFboNewProductionDateTextChanged;
        _fboNewProductionDateTextBox = textBox;
        if (textBox is not null)
            textBox.TextChanged += OnFboNewProductionDateTextChanged;
    }

    private void OnFboNewProductionDateTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_fboNewFormattingProductionDate || sender is not DatePickerTextBox textBox)
            return;
        var digits = new string((textBox.Text ?? "").Where(char.IsDigit).Take(8).ToArray());
        var formatted = FormatFboNewProductionDate(digits);
        if (!string.Equals(textBox.Text, formatted, StringComparison.Ordinal))
        {
            _fboNewFormattingProductionDate = true;
            textBox.Text = formatted;
            textBox.CaretIndex = formatted.Length;
            _fboNewFormattingProductionDate = false;
        }
        if (digits.Length != 8 ||
            !DateTime.TryParseExact(digits, "ddMMyyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return;
        if (FboNewProductionDate.SelectedDate != date)
            FboNewProductionDate.SelectedDate = date;
    }

    private static string FormatFboNewProductionDate(string digits)
    {
        if (digits.Length <= 1)
            return digits;
        if (digits.Length == 2)
            return digits + ".";
        if (digits.Length == 3)
            return digits[..2] + "." + digits[2..];
        if (digits.Length == 4)
            return digits[..2] + "." + digits[2..] + ".";
        return digits[..2] + "." + digits.Substring(2, 2) + "." + digits[4..];
    }

    private void OnFboNewProductionDateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fboNewChangingProductionDate || FboNewProductionDate.SelectedDate is null)
            return;
        Dispatcher.BeginInvoke(FocusFboNewQuantity, DispatcherPriority.Input);
    }

    private void OnFboNewProductionDateKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || FboNewProductionDate.SelectedDate is null)
            return;
        e.Handled = true;
        FocusFboNewQuantity();
    }

    private void OnFboNewQtyKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        FocusFboNewScan();
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
        var allFree = FboNewPrintAllFree.IsChecked == true;
        await FboNewRunAsync(async () =>
        {
            var payload = await _client.FboSheetPrintBoxesAsync(jobId, count, allFree);
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

    private async void OnFboNewPrintSupplyQr(object sender, RoutedEventArgs e)
    {
        if (_fboNewJob is null)
        {
            MessageBox.Show(this, "Сначала откройте задание", "FBO WB new");
            return;
        }
        var copies = ShowFboNewSupplyQrDialog();
        if (copies is null) return;
        var jobId = _fboNewJob.Int("id");
        await FboNewRunAsync(async () =>
        {
            var pdf = await _client.FboSheetDownloadSupplyQrAsync(jobId);
            await Task.Run(() => GdiPrinter.PrintPdf(pdf, _config.LabelProfile(), copies.Value));
            SetStatus($"QR поставки отправлен на печать: {copies.Value} шт.");
            FocusFboNewScan();
        }, "Печать QR поставки...");
    }

    private int? ShowFboNewSupplyQrDialog()
    {
        var dialog = CreateFboNewPrintDialog("Печать QR поставки", 360);
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = "Количество QR",
            Margin = new Thickness(0, 0, 0, 5),
        });
        var copiesBox = new TextBox
        {
            Text = "1",
            FontSize = 16,
            Margin = new Thickness(0, 0, 0, 14),
        };
        panel.Children.Add(copiesBox);

        var result = default(int?);
        panel.Children.Add(CreateFboNewDialogButtons(
            dialog,
            () =>
            {
                if (!int.TryParse(copiesBox.Text.Trim(), out var copies) || copies < 1 || copies > 9999)
                {
                    MessageBox.Show(dialog, "Укажите количество QR от 1 до 9999", "FBO WB new");
                    copiesBox.Focus();
                    copiesBox.SelectAll();
                    return;
                }
                result = copies;
                dialog.DialogResult = true;
            }));
        dialog.Content = panel;
        dialog.Loaded += (_, _) =>
        {
            copiesBox.Focus();
            copiesBox.SelectAll();
        };
        return dialog.ShowDialog() == true ? result : null;
    }

    private Window CreateFboNewPrintDialog(string title, double width) => new()
    {
        Title = title,
        Width = width,
        SizeToContent = SizeToContent.Height,
        ResizeMode = ResizeMode.NoResize,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        Owner = this,
        ShowInTaskbar = false,
    };

    private static StackPanel CreateFboNewDialogButtons(Window dialog, Action confirm)
    {
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var printButton = new Button
        {
            Content = "Печать",
            IsDefault = true,
            MinWidth = 100,
            Padding = new Thickness(12, 6, 12, 6),
        };
        printButton.Click += (_, _) => confirm();
        var cancelButton = new Button
        {
            Content = "Отмена",
            IsCancel = true,
            MinWidth = 100,
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(8, 0, 0, 0),
        };
        cancelButton.Click += (_, _) => dialog.Close();
        buttons.Children.Add(printButton);
        buttons.Children.Add(cancelButton);
        return buttons;
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
                ScanSounds.Ok();
                SetStatus($"Товар {product.Str("sku")}");
                return;
            }
            if (kind == "wb_box")
            {
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
                if (remaining is not null)
                    _fboNewProduct = remaining;
                else
                    SyncFboNewSelectedProduct();
                ApplyFboNewProductionDate(_fboNewProduct);
                RefreshFboNewSelectedText();
                RenderFboNewJob();
                RenderFboNewJobs();
                ScanSounds.Ok();
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
        ScanSounds.Error();
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
        if (sender is not FrameworkElement host)
            return;
        if (ContextMenuOwnedByDescendant(host, e.OriginalSource as DependencyObject))
            return;
        var data = OverviewDataFromSource(e.OriginalSource as DependencyObject) ?? host.DataContext;
        var menu = host.ContextMenu;
        if (menu is null)
            return;
        menu.Tag = data;
        var unassign = CanUnassignOverview(data);
        if (!unassign)
        {
            e.Handled = true;
            return;
        }
    }

    private static bool ContextMenuOwnedByDescendant(FrameworkElement host, DependencyObject? origin)
    {
        for (var current = origin; current != null && !ReferenceEquals(current, host); current = ParentOf(current))
        {
            if (current is FrameworkElement fe &&
                fe.ContextMenu != null &&
                !ReferenceEquals(fe, host))
                return true;
        }
        return false;
    }

    private static object? OverviewDataFromSource(DependencyObject? origin)
    {
        for (var current = origin; current != null; current = ParentOf(current))
        {
            if (current is FrameworkElement fe && fe.DataContext is FboOverviewLineRow)
                return fe.DataContext;
        }
        return null;
    }

    private static DependencyObject? ParentOf(DependencyObject current) =>
        current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current);

    private static bool CanUnassignOverview(object? data) =>
        (data is FboOverviewLineRow line && line.CanUnassign)
        || (data is FboOverviewGroupRow group && group.CanUnassign);

    private static bool TryOverviewBox(object? data, out int boxId, out string boxCode, out string productBarcode, out string palletCode)
    {
        if (data is FboOverviewLineRow line && line.BoxId > 0)
        {
            boxId = line.BoxId;
            boxCode = line.BoxCode;
            productBarcode = line.ProductBarcode;
            palletCode = line.PalletCode;
            return true;
        }
        if (data is FboOverviewGroupRow group && group.BoxId > 0)
        {
            boxId = group.BoxId;
            boxCode = group.BoxCode;
            productBarcode = group.ProductBarcode;
            palletCode = group.PalletCode;
            return true;
        }
        boxId = 0;
        boxCode = "";
        productBarcode = "";
        palletCode = "";
        return false;
    }

    private static object? OverviewMenuData(object sender)
    {
        if (sender is MenuItem item && item.Parent is ContextMenu menu)
        {
            if (menu.Tag is FboOverviewLineRow or FboOverviewGroupRow)
                return menu.Tag;
            if (menu.PlacementTarget is FrameworkElement target)
                return target.DataContext;
        }
        return (sender as FrameworkElement)?.DataContext;
    }

    private async void OnFboNewUnassignClick(object sender, RoutedEventArgs e)
    {
        var data = OverviewMenuData(sender);
        if (!TryOverviewBox(data, out var boxId, out var boxCode, out var productBarcode, out _) ||
            !CanUnassignOverview(data))
            return;
        if (_fboNewJob is null)
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
    }

    private static string BoxTitle(JsonMap box)
    {
        var id = box.Str("box_id");
        if (id.Length == 0) id = box.Str("order_display");
        if (id.Length == 0) id = $"№{box.Int("seq")}";
        return id;
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
