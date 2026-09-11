using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WarehousePacking.Models;
using WarehousePacking.Services;

namespace WarehousePacking.Views;

public partial class MainWindow
{
    private bool RequireFboApi()
    {
        if (_client.ApiOk)
        {
            FboApiHint.Text = $"API: {_client.ApiUrl}";
            FboApiHint.Foreground = Brushes.DarkGreen;
            return true;
        }
        FboApiHint.Text = string.IsNullOrWhiteSpace(_client.ApiError)
            ? "Нет сессии API — нужен run_api.py"
            : _client.ApiError;
        FboApiHint.Foreground = Brushes.Firebrick;
        return false;
    }

    private void FocusFboScan() => FboScanBox.Focus();

    private async void OnReloadFbo(object sender, RoutedEventArgs e) => await LoadFboJobsAsync();

    private async Task LoadFboJobsAsync()
    {
        if (!RequireFboApi())
        {
            MessageBox.Show(this, string.IsNullOrWhiteSpace(_client.ApiError)
                ? "Запустите python run_api.py (порт 8766) и укажите адрес API в настройках."
                : _client.ApiError, "API упаковщиков");
            return;
        }
        SetStatus("Загрузка заданий FBO...");
        try
        {
            _fboJobs = await _client.FboMyJobsAsync();
            var selected = _fboPendingJobId > 0 ? _fboPendingJobId : _fboJob?.IntOrNull("id");
            if (selected is int sid)
            {
                var idx = _fboJobs.FindIndex(j => j.Int("id") == sid);
                if (idx >= 0) _fboJobsPage = idx / Paging.PageSize;
            }
            RenderFboJobs();
            SetStatus($"FBO заданий: {_fboJobs.Count}");
            FocusFboScan();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void RenderFboJobs()
    {
        var (visible, page) = Paging.Slice(_fboJobs, _fboJobsPage);
        _fboJobsPage = page;
        _fboJobsFilling = true;
        _fboJobRows.Clear();
        foreach (var j in visible)
        {
            _fboJobRows.Add(new FbsJobRow
            {
                Id = j.Int("id"),
                Status = Paging.JobStatusRu(j.Str("status")),
                Progress = $"{j.Int("line_done")}/{j.Int("line_total")}",
            });
        }
        var selected = _fboPendingJobId > 0 ? _fboPendingJobId : _fboJob?.IntOrNull("id");
        if (selected is int sid)
            FboJobsGrid.SelectedItem = _fboJobRows.FirstOrDefault(x => x.Id == sid);
        _fboJobsFilling = false;
        FboJobsPageLabel.Text = Paging.RangeLabel(_fboJobs.Count, _fboJobsPage);
        FboJobsPrev.IsEnabled = _fboJobsPage > 0;
        FboJobsNext.IsEnabled = _fboJobsPage + 1 < Paging.PageCount(_fboJobs.Count);
    }

    private void OnFboJobsPrev(object sender, RoutedEventArgs e)
    {
        if (_fboJobsPage > 0) { _fboJobsPage--; RenderFboJobs(); }
    }

    private void OnFboJobsNext(object sender, RoutedEventArgs e)
    {
        if (_fboJobsPage + 1 < Paging.PageCount(_fboJobs.Count)) { _fboJobsPage++; RenderFboJobs(); }
    }

    private void OnFboJobSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_fboJobsFilling || FboJobsGrid.SelectedItem is not FbsJobRow row) return;
        _fboPendingJobId = row.Id;
        _fboSelectTimer.Stop();
        _fboSelectTimer.Start();
    }

    private async Task OpenFboJobAsync(int jobId)
    {
        var previous = _fboOpenCts;
        var cts = new CancellationTokenSource();
        _fboOpenCts = cts;
        previous?.Cancel();
        SetStatus($"Открытие задания #{jobId}...");
        try
        {
            var job = await _client.FboOpenJobAsync(jobId, cts.Token);
            if (!ReferenceEquals(_fboOpenCts, cts)) return;
            _fboJob = job;
            RenderFboJob();
            FocusFboScan();
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_fboOpenCts, cts)) ShowFboError(ex);
        }
        finally
        {
            if (ReferenceEquals(_fboOpenCts, cts))
            {
                _fboOpenCts = null;
                cts.Dispose();
            }
        }
    }

    private void RenderFboJob()
    {
        var job = _fboJob;
        var title = job is null ? "Выберите задание" : $"Поставка {job.Str("supply_id")} · #{job.Str("id")}";
        if (job is not null && job.Str("city").Length > 0)
            title += $" · {job.Str("city")}";
        FboJobTitle.Text = title;
        var done = job?.Int("line_done") ?? 0;
        var total = job?.Int("line_total") ?? 0;
        var pending = job?.Int("line_pending", job?.Int("remaining") ?? 0) ?? 0;
        var printed = job?.Int("line_printed") ?? 0;
        FboJobStats.Text = $"готово {done}/{total} · осталось {pending} · в печати {printed}";
        var qr = job?.Str("supply_qr_code") ?? "";
        FboCacheHint.Text = qr.Length > 0 ? qr : (job?.Flag("has_supply_qr") == true ? "QR поставки прикреплён" : "");
        var actives = FboActiveLines();
        var skip = FboSkipMpBox.IsChecked == true;
        if (actives.Count > 0)
        {
            FboActiveText.Text = Paging.FormatPicked(actives, skip);
            SetFboActiveImage(actives[0].Str("image_url"));
        }
        else
        {
            var hint = skip ? "пикните товар (без подтверждения ШК МП)" : "пикните товар";
            FboActiveText.Text = $"Нет активной строки — {hint}";
            SetFboActiveImage("");
        }
        var jid = job?.IntOrNull("id");
        if (!Equals(_fboPagedJobId, jid))
        {
            _fboLinesPage = 0;
            _fboPagedJobId = jid;
        }
        if (actives.Count > 0)
        {
            var aid = actives[0].Str("id");
            var idx = (job?.Arr("lines") ?? []).FindIndex(l => l.Str("id") == aid);
            if (idx >= 0) _fboLinesPage = idx / Paging.PageSize;
        }
        RenderFboLines();
        if (FboManualBox.IsChecked == true)
            RenderFboRemaining();
    }

    private void SetFboActiveImage(string url)
    {
        var raw = (url ?? "").Trim();
        _fboActiveImageUrl = raw;
        FboActiveImage.Source = null;
        if (raw.Length == 0) return;
        _photos.Load(raw, 110, img =>
        {
            if (_fboActiveImageUrl == raw) FboActiveImage.Source = img;
        });
    }

    private List<JsonMap> FboActiveLines()
    {
        var job = _fboJob;
        if (job is null) return [];
        var lines = job.Arr("active_lines");
        if (lines.Count > 0) return lines;
        var one = job.Obj("active_line");
        return one is null ? [] : [one];
    }

    private void RenderFboLines()
    {
        var job = _fboJob;
        var lines = job?.Arr("lines") ?? [];
        var (visible, page) = Paging.Slice(lines, _fboLinesPage);
        _fboLinesPage = page;
        _fboLineRows.Clear();
        foreach (var line in visible)
        {
            var row = new FbsLineRow
            {
                Id = line.Int("id"),
                Seq = line.Str("seq"),
                Sku = line.Str("sku"),
                Name = line.Str("product_name"),
                Order = line.Str("order_display", line.Str("box_id")),
                Status = Paging.LineStatusRu(line.Str("status")),
            };
            _fboLineRows.Add(row);
            _photos.Load(line.Str("image_url"), 34, img => row.Photo = img);
        }
        FboLinesPageLabel.Text = Paging.RangeLabel(lines.Count, _fboLinesPage);
        FboLinesPrev.IsEnabled = _fboLinesPage > 0;
        FboLinesNext.IsEnabled = _fboLinesPage + 1 < Paging.PageCount(lines.Count);
    }

    private void OnFboLinesPrev(object sender, RoutedEventArgs e)
    {
        if (_fboLinesPage > 0) { _fboLinesPage--; RenderFboLines(); }
    }

    private void OnFboLinesNext(object sender, RoutedEventArgs e)
    {
        var total = _fboJob?.Arr("lines").Count ?? 0;
        if (_fboLinesPage + 1 < Paging.PageCount(total)) { _fboLinesPage++; RenderFboLines(); }
    }

    private void OnFboManualToggle(object sender, RoutedEventArgs e)
    {
        FboManualPanel.Visibility = FboManualBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (FboManualBox.IsChecked == true) RenderFboRemaining();
        FocusFboScan();
    }

    private void RenderFboRemaining()
    {
        var groups = _fboJob?.Arr("remaining_groups") ?? [];
        _fboRemainingRows.Clear();
        for (var i = 0; i < groups.Count; i++)
        {
            var g = groups[i];
            var row = new RemainingRow
            {
                Index = i,
                Sku = g.Str("sku"),
                Name = g.Str("name"),
                Qty = g.Str("quantity"),
                Barcode = g.Str("barcode"),
            };
            _fboRemainingRows.Add(row);
            _photos.Load(g.Str("image_url"), 34, img => row.Photo = img);
        }
        if (_fboRemainingRows.Count > 0)
            FboRemainingGrid.SelectedIndex = 0;
        _fboSelectedGroup = groups.Count > 0 ? groups[0] : null;
    }

    private async void OnFboRemainingPick(object sender, RoutedEventArgs e) => await PickFboRemainingAsync();
    private async void OnFboRemainingPick(object sender, MouseButtonEventArgs e) => await PickFboRemainingAsync();

    private async Task PickFboRemainingAsync()
    {
        if (FboRemainingGrid.SelectedItem is not RemainingRow row || _fboJob is null) return;
        var groups = _fboJob.Arr("remaining_groups");
        if (row.Index < 0 || row.Index >= groups.Count) return;
        _fboSelectedGroup = groups[row.Index];
        var sku = _fboSelectedGroup.Str("sku");
        var pid = _fboSelectedGroup.IntOrNull("product_id");
        var jobId = _fboJob.Int("id");
        await HandleFboAllocateAsync(
            () => _client.FboPickSkuAsync(jobId, sku, pid, FboBatchBox.IsChecked == true, true, FboSkipMpBox.IsChecked == true),
            "Пик товара...",
            sku.ToLowerInvariant());
    }

    private void OnFboScanKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _ = OnFboScanAsync();
        }
    }

    private async void OnFboScan(object sender, RoutedEventArgs e) => await OnFboScanAsync();

    private async Task OnFboScanAsync()
    {
        var code = FboScanBox.Text.Trim();
        FboScanBox.Text = "";
        if (code.Length == 0) return;
        if (_fboJob is null)
        {
            MessageBox.Show(this, "Сначала откройте задание", "FBO");
            return;
        }
        var jobId = _fboJob.Int("id");
        var scanKey = code.ToLowerInvariant();
        if (FboSkipMpBox.IsChecked == true && scanKey.Length > 0 && scanKey == _fboLastScanCode && _fboLastScanLineIds.Count > 0 && !FboSkuHasPending(_fboLastScanSku))
        {
            if (MessageBox.Show(this, "Распечатать заново?", "FBO", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                await ReprintFboLastAsync(jobId);
            FocusFboScan();
            return;
        }
        await HandleFboAllocateAsync(
            () => _client.FboScanProductAsync(jobId, code, FboBatchBox.IsChecked == true, true, FboSkipMpBox.IsChecked == true),
            "Пик товара...",
            scanKey);
    }

    private bool FboSkuHasPending(string sku)
    {
        var want = (sku ?? "").Trim().ToLowerInvariant();
        if (want.Length == 0 || _fboJob is null) return false;
        if (_fboJob.Arr("lines").Any(l => l.Str("sku").Trim().ToLowerInvariant() == want && l.Str("status") == "pending"))
            return true;
        return _fboJob.Arr("remaining_groups").Any(g => g.Str("sku").Trim().ToLowerInvariant() == want && g.Int("quantity") > 0);
    }

    private async Task ReprintFboLastAsync(int jobId)
    {
        if (_fboLastScanLineIds.Count == 0)
        {
            MessageBox.Show(this, "Нет последней строки для перепечатки", "FBO");
            return;
        }
        await FboRunAsync(async () =>
        {
            var pdfs = await ResolveFboPdfsAsync(jobId, _fboLastScanLineIds, null);
            foreach (var pdf in pdfs)
                GdiPrinter.PrintPdf(pdf, _config.LabelProfile());
            SetStatus($"Ярлык перепечатан ({pdfs.Count})");
            FocusFboScan();
        }, "Перепечатка...");
    }

    private async void OnFboReprint(object sender, RoutedEventArgs e)
    {
        var actives = FboActiveLines();
        if (_fboJob is null || actives.Count == 0)
        {
            MessageBox.Show(this, "Нет активной строки", "FBO");
            return;
        }
        var jobId = _fboJob.Int("id");
        var ids = actives.Select(x => x.Int("id")).ToList();
        await FboRunAsync(async () =>
        {
            var pdfs = await ResolveFboPdfsAsync(jobId, ids, null);
            foreach (var pdf in pdfs)
                GdiPrinter.PrintPdf(pdf, _config.LabelProfile());
            SetStatus($"На повторную печать: {pdfs.Count} ярл.");
            FocusFboScan();
        }, "Перепечатка...");
    }

    private async void OnFboCancelPrint(object sender, RoutedEventArgs e)
    {
        var active = FboActiveLines().FirstOrDefault();
        if (_fboJob is null || active is null)
        {
            MessageBox.Show(this, "Нет активной строки", "FBO");
            return;
        }
        await FboRunAsync(async () =>
        {
            var payload = await _client.FboCancelPrintAsync(_fboJob.Int("id"), active.Int("id"));
            _fboJob = payload.Obj("job") ?? _fboJob;
            RenderFboJob();
            SetStatus("Печать отменена");
            FocusFboScan();
        }, "Отмена печати...");
    }

    private async void OnDownloadFboLabels(object sender, RoutedEventArgs e)
    {
        if (_fboJob is null)
        {
            MessageBox.Show(this, "Сначала откройте задание", "FBO");
            return;
        }
        if (!RequireFboApi()) return;
        var jobId = _fboJob.Int("id");
        if (jobId == 0 || _fboBusy) return;
        _fboBusy = true;
        SetStatus("Скачивание ярлыков...");
        try
        {
            var zip = await _client.FboDownloadLineLabelsZipAsync(jobId);
            var saved = await Task.Run(() => LabelCache.SaveZip(jobId, zip, LabelCache.FboRoot));
            SetStatus($"Ярлыки сохранены локально: {saved}");
            FocusFboScan();
        }
        catch (Exception ex) { ShowFboError(ex); }
        finally { _fboBusy = false; }
    }

    private async void OnPrintFboSupplyQr(object sender, RoutedEventArgs e)
    {
        if (_fboJob is null)
        {
            MessageBox.Show(this, "Сначала откройте задание", "FBO");
            return;
        }
        await FboRunAsync(async () =>
        {
            var pdf = await _client.FboDownloadSupplyQrAsync(_fboJob.Int("id"));
            GdiPrinter.PrintPdf(pdf, _config.LabelProfile());
            SetStatus("QR поставки отправлен на печать");
            FocusFboScan();
        }, "Печать QR поставки...");
    }

    private async void OnPrintFboPalletSheets(object sender, RoutedEventArgs e)
    {
        if (_fboJob is null)
        {
            MessageBox.Show(this, "Сначала откройте задание", "FBO");
            return;
        }
        await FboRunAsync(async () =>
        {
            var pdf = await _client.FboDownloadPalletSheetsAsync(_fboJob.Int("id"));
            GdiPrinter.PrintPdf(pdf, _config.A4Profile());
            SetStatus("Листы паллет отправлены на печать");
            FocusFboScan();
        }, "Печать листов...");
    }

    private async Task<List<byte[]>> ResolveFboPdfsAsync(int jobId, List<int> lineIds, List<string>? payloadPdfs)
    {
        var pdfs = new List<byte[]>();
        for (var i = 0; i < lineIds.Count; i++)
        {
            var cached = LabelCache.GetLine(jobId, lineIds[i], LabelCache.FboRoot);
            if (cached != null) { pdfs.Add(cached); continue; }
            if (payloadPdfs != null && i < payloadPdfs.Count && payloadPdfs[i].Length > 0)
            {
                var pdf = Convert.FromBase64String(payloadPdfs[i]);
                LabelCache.PutLine(jobId, lineIds[i], pdf, LabelCache.FboRoot);
                pdfs.Add(pdf);
                continue;
            }
            var downloaded = await _client.FboDownloadLinePdfAsync(jobId, lineIds[i]);
            LabelCache.PutLine(jobId, lineIds[i], downloaded, LabelCache.FboRoot);
            pdfs.Add(downloaded);
        }
        return pdfs;
    }

    private async Task HandleFboAllocateAsync(Func<Task<JsonMap>> worker, string status, string scanCode)
    {
        if (_fboJob is null) return;
        var jobId = _fboJob.Int("id");
        var skip = FboSkipMpBox.IsChecked == true;
        var profile = _config.LabelProfile();
        await FboRunAsync(async () =>
        {
            var payload = await worker();
            var lines = payload.Arr("lines");
            if (lines.Count == 0 && payload.Obj("line") is { } one) lines = [one];
            var payloadPdfs = payload.StrList("pdfs_base64");
            if (payloadPdfs.Count == 0 && payload.Str("pdf_base64").Length > 0)
                payloadPdfs = [payload.Str("pdf_base64")];
            var lineIds = lines.Where(l => l.IntOrNull("id") is not null).Select(l => l.Int("id")).ToList();
            var pdfs = jobId > 0 ? await ResolveFboPdfsAsync(jobId, lineIds, payloadPdfs) : [];
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
                    JsonMap? last = null;
                    foreach (var line in needClose)
                        last = await _client.FboCloseLineAsync(jobId, line.Int("id"));
                    if (last is not null)
                        payload = last;
                }
                catch (Exception ex) { closeError = ex; }
            }
            _fboJob = payload.Obj("job") ?? _fboJob;
            _fboLastScanCode = scanCode;
            _fboLastScanSku = lines.FirstOrDefault()?.Str("sku") ?? "";
            _fboLastScanLineIds = lineIds;
            RenderFboJob();
            SetStatus(printed > 0 ? $"Напечатано этикеток: {printed}" : "Строка выделена");
            if (printError is not null) throw printError;
            if (closeError is not null) throw closeError;
            FocusFboScan();
        }, status);
    }

    private JsonMap? SelectedFboLine()
    {
        if (FboLinesGrid.SelectedItem is not FbsLineRow row || _fboJob is null) return null;
        return _fboJob.Arr("lines").FirstOrDefault(l => l.Int("id") == row.Id);
    }

    private void OnFboLinesContextOpening(object sender, ContextMenuEventArgs e)
    {
        var line = SelectedFboLine();
        if (line is null || FboLinesGrid.ContextMenu?.Items.Count is not > 0)
        {
            e.Handled = true;
            return;
        }
        if (FboLinesGrid.ContextMenu.Items[0] is MenuItem item)
            item.Header = line.Str("status") is "printed" or "done" ? "Статус: в сборке" : "Статус: готово";
    }

    private async void OnFboLineStatusToggle(object sender, RoutedEventArgs e)
    {
        var line = SelectedFboLine();
        if (line is null || _fboJob is null) return;
        if (!RequireFboApi()) return;
        var lineId = line.Int("id");
        var next = line.Str("status") is "printed" or "done" ? "pending" : "done";
        await FboRunAsync(async () =>
        {
            var payload = await _client.FboSetLineStatusAsync(_fboJob.Int("id"), lineId, next);
            _fboJob = payload.Obj("job") ?? _fboJob;
            RenderFboJob();
            SetStatus($"Статус строки: {Paging.LineStatusRu(next)}");
            FocusFboScan();
        }, "Смена статуса...");
    }

    private async Task FboRunAsync(Func<Task> work, string status)
    {
        if (_fboBusy)
        {
            SetStatus("Предыдущая операция ещё выполняется");
            return;
        }
        _fboBusy = true;
        SetStatus(status);
        try { await work(); }
        catch (Exception ex) { ShowFboError(ex); }
        finally { _fboBusy = false; }
    }

    private void ShowFboError(Exception ex)
    {
        if (ex is OperationCanceledException) return;
        if (ex is AuthException) { ShowError(ex); return; }
        if (ex is ApiException)
        {
            MessageBox.Show(this, ex.Message, "FBO", MessageBoxButton.OK, MessageBoxImage.Warning);
            SetStatus(ex.Message);
            FocusFboScan();
            return;
        }
        ShowError(ex);
        FocusFboScan();
    }
}
