using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using WarehousePacking.Models;
using WarehousePacking.Services;

namespace WarehousePacking.Views;

public partial class MainWindow
{
    private bool _omBusy;
    private bool _omJobsFilling;
    private int _omLinesPage;
    private DispatcherTimer? _omSearchTimer;
    private List<JsonMap> _omJobs = [];
    private JsonMap? _omJob;
    private JsonMap? _omSelectedGroup;
    private readonly ObservableCollection<FbsJobRow> _omJobRows = [];
    private readonly ObservableCollection<FbsLineRow> _omLineRows = [];
    private readonly ObservableCollection<RemainingRow> _omRemainingRows = [];

    private async void OnReloadOtherMp(object sender, RoutedEventArgs e) => await LoadOtherMpJobsAsync();

    private async Task LoadOtherMpJobsAsync()
    {
        if (_omBusy) return;
        _omBusy = true;
        try
        {
            _omJobs = await _client.OtherMpMyJobsAsync();
            _omJobsFilling = true;
            _omJobRows.Clear();
            foreach (var job in _omJobs)
            {
                _omJobRows.Add(new FbsJobRow
                {
                    Id = job.Int("id"),
                    Status = job.Str("order_number"),
                    Progress = $"{job.Int("line_done")}/{job.Int("line_total")}",
                });
            }
            _omJobsFilling = false;
            SetStatus($"Прочие маркетплейсы: заданий {_omJobs.Count}");
        }
        catch (Exception ex)
        {
            _omJobsFilling = false;
            SetStatus(ex.Message);
        }
        finally
        {
            _omBusy = false;
        }
    }

    private async void OnOmJobSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_omJobsFilling) return;
        if (OmJobsGrid.SelectedItem is not FbsJobRow row) return;
        await OpenOmJobAsync(row.Id);
    }

    private async Task OpenOmJobAsync(int jobId)
    {
        try
        {
            _omLinesPage = 0;
            OmLinesSearch.Text = "";
            OmActiveText.Text = "Нет активного товара — пикните товар. Ярлык заказа сам не печатается.";
            OmActiveImage.Source = null;
            _omJob = await _client.OtherMpOpenJobAsync(jobId);
            RenderOmJob();
            OmScanBox.Focus();
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    private void RenderOmJob()
    {
        var job = _omJob;
        var purchase = job?.Str("purchase_status") ?? "";
        OmJobTitle.Text = job is null
            ? "Выберите задание"
            : $"Заказ {job.Str("order_number")} · перемещение {job.Str("transfer_number")}"
              + (purchase.Length > 0 ? $" · {purchase}" : "");
        var done = job?.Int("line_done") ?? 0;
        var total = job?.Int("line_total") ?? 0;
        OmJobStats.Text = $"готово {done}/{total}";
        var needsCis = (job?.Arr("lines") ?? []).Any(line => line.Flag("require_cis") && line.Str("status") != "done");
        OmCisHint.Text = needsCis
            ? "Есть товары с маркировкой: по ним пикайте КИЗ, обычный штрихкод не закроет строку."
            : "";
        RenderOmLines();
        if (OmManualBox.IsChecked == true) RenderOmRemaining();
    }

    private List<JsonMap> FilteredOmLines()
    {
        var job = _omJob;
        if (job is null) return [];
        var query = OmLinesSearch.Text.Trim();
        var lines = job.Arr("lines");
        if (query.Length == 0) return lines;
        return lines.Where(line => Paging.JobLineMatches(line, query, line.Str("excel_barcode"))).ToList();
    }

    private void RenderOmLines()
    {
        var lines = FilteredOmLines();
        var (visible, page) = Paging.Slice(lines, _omLinesPage);
        _omLinesPage = page;
        _omLineRows.Clear();
        foreach (var line in visible)
        {
            var row = new FbsLineRow
            {
                Id = line.Int("id"),
                Seq = line.Str("seq"),
                Sku = line.Str("sku"),
                Name = line.Str("product_name"),
                Order = $"{line.Int("picked_qty")}/{line.Int("quantity")}",
                Status = Paging.LineStatusRu(line.Str("status")),
            };
            _omLineRows.Add(row);
            _photos.Load(line.Str("image_url"), 34, img => row.Photo = img);
        }
        OmLinesPageLabel.Text = Paging.RangeLabel(lines.Count, _omLinesPage);
        OmLinesPrev.IsEnabled = _omLinesPage > 0;
        OmLinesNext.IsEnabled = _omLinesPage + 1 < Paging.PageCount(lines.Count);
    }

    private void OnOmLinesPrev(object sender, RoutedEventArgs e)
    {
        if (_omLinesPage > 0)
        {
            _omLinesPage--;
            RenderOmLines();
        }
    }

    private void OnOmLinesNext(object sender, RoutedEventArgs e)
    {
        if (_omLinesPage + 1 < Paging.PageCount(FilteredOmLines().Count))
        {
            _omLinesPage++;
            RenderOmLines();
        }
    }

    private void OnOmLinesSearch(object sender, RoutedEventArgs e)
    {
        _omLinesPage = 0;
        RenderOmLines();
        if (OmManualBox.IsChecked == true) RenderOmRemaining();
    }

    private void OnOmLinesSearchKey(object sender, KeyEventArgs e)
    {
        _omSearchTimer?.Stop();
        _omSearchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _omSearchTimer.Tick += (_, _) =>
        {
            _omSearchTimer.Stop();
            OnOmLinesSearch(sender, e);
        };
        _omSearchTimer.Start();
    }

    private void OnOmManualToggle(object sender, RoutedEventArgs e)
    {
        OmManualPanel.Visibility = OmManualBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (OmManualBox.IsChecked == true) RenderOmRemaining();
        OmScanBox.Focus();
    }

    private void RenderOmRemaining()
    {
        var groups = _omJob?.Arr("remaining_groups") ?? [];
        var query = OmLinesSearch.Text.Trim();
        _omRemainingRows.Clear();
        for (var i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            if (query.Length > 0 && !Paging.RemainingMatches(group, query)) continue;
            var row = new RemainingRow
            {
                Index = i,
                Sku = group.Str("sku"),
                Name = group.Str("name"),
                Qty = group.Str("quantity", group.Str("qty")),
                Barcode = group.Str("barcode"),
            };
            _omRemainingRows.Add(row);
            _photos.Load(group.Str("image_url"), 34, img => row.Photo = img);
        }
        if (_omRemainingRows.Count > 0)
        {
            OmRemainingGrid.SelectedIndex = 0;
            SyncOmSelectedGroup();
        }
        else
        {
            _omSelectedGroup = null;
            ShowOmBarcode("");
        }
    }

    private void OnOmRemainingSelected(object sender, SelectionChangedEventArgs e) => SyncOmSelectedGroup();

    private void SyncOmSelectedGroup()
    {
        if (OmRemainingGrid.SelectedItem is not RemainingRow row || _omJob is null)
        {
            _omSelectedGroup = null;
            ShowOmBarcode("");
            return;
        }
        var groups = _omJob.Arr("remaining_groups");
        if (row.Index < 0 || row.Index >= groups.Count) return;
        _omSelectedGroup = groups[row.Index];
        ShowOmBarcode(_omSelectedGroup.Str("barcode"));
    }

    private void ShowOmBarcode(string code)
    {
        code = (code ?? "").Trim();
        if (code.Length == 0)
        {
            OmBarcodeImage.Source = null;
            return;
        }
        try
        {
            using var bmp = BarcodeLabel.RenderCode128(code, 170, 70);
            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            OmBarcodeImage.Source = PhotoLoader.Decode(ms.ToArray());
        }
        catch
        {
            OmBarcodeImage.Source = null;
        }
    }

    private async void OnOmScan(object sender, RoutedEventArgs e) => await ScanOmAsync();

    private async void OnOmScanKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter && e.Key != Key.Return) return;
        e.Handled = true;
        await ScanOmAsync();
    }

    private async Task ScanOmAsync()
    {
        if (_omJob is null)
        {
            SetStatus("Выберите задание");
            return;
        }
        var code = OmScanBox.Text.Trim();
        if (code.Length == 0) return;
        OmScanBox.Clear();
        try
        {
            var result = await _client.OtherMpScanAsync(_omJob.Int("id"), code);
            await ApplyOmPickAsync(result);
        }
        catch (Exception ex)
        {
            var text = string.IsNullOrWhiteSpace(ex.Message)
                ? $"Штрихкод «{code}» не принят"
                : ex.Message;
            ShowOmScanWarning(text, "Сканер", replaceActive: true);
        }
    }

    private void ShowOmScanWarning(string text, string title, bool replaceActive)
    {
        OmScanWarning.Text = text;
        if (replaceActive)
            OmActiveText.Text = text;
        SetStatus(text);
        MessageBox.Show(this, text, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        OmScanBox.Focus();
    }

    private async void OnOmRemainingPick(object sender, RoutedEventArgs e)
    {
        if (_omJob is null || _omSelectedGroup is null) return;
        if (_omSelectedGroup.Flag("require_cis"))
        {
            MessageBox.Show(this, "Для этого товара нужен КИЗ. Пикните код честного знака.", "Честный знак");
            return;
        }
        try
        {
            var result = await _client.OtherMpPickAsync(
                _omJob.Int("id"),
                _omSelectedGroup.Str("sku"),
                _omSelectedGroup.IntOrNull("product_id"));
            await ApplyOmPickAsync(result);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    private async Task ApplyOmPickAsync(JsonMap result)
    {
        var job = result.Obj("job");
        if (job is not null) _omJob = job;
        RenderOmJob();
        var copies = result.Int("barcode_copies");
        var barcode = result.Str("barcode");
        var warning = result.Str("warning");
        var mismatch = result.Flag("mismatch");
        if (mismatch && string.IsNullOrWhiteSpace(warning))
            warning = $"Штрихкода «{barcode}» нет в файле поставки. Строка принята, код записан в лист несовпадений.";
        var lineId = result.Int("line_id");
        var line = (_omJob?.Arr("lines") ?? []).FirstOrDefault(item => item.Int("id") == lineId);
        var sku = line?.Str("sku") ?? "";
        var name = line?.Str("product_name") ?? "";
        OmActiveText.Text = $"{sku} · {name} · ШК поставки {barcode} · {copies} шт.";
        OmActiveImage.Source = null;
        if (line is not null)
            _photos.Load(line.Str("image_url"), 72, img => OmActiveImage.Source = img);
        if (OmPrintBarcodes.IsChecked == true && copies > 0 && barcode.Length > 0)
        {
            var size = PrintOptions.LabelSizeMm(_config.LabelSettings);
            var pdf = BarcodeLabel.LabelPdf(barcode, sku, name, size.WidthMm, size.HeightMm);
            await RunPrint("Печать шк...", async () =>
            {
                await Task.Run(() => GdiPrinter.PrintPdf(pdf, _config.LabelProfile(), copies));
            });
        }
        if (mismatch)
            ShowOmScanWarning(warning, "Штрихкод не из поставки", replaceActive: false);
        else
        {
            OmScanWarning.Text = "";
            SetStatus(copies > 0 ? $"ШК поставки {barcode}: {copies} шт." : "Строка отмечена");
            OmScanBox.Focus();
        }
    }

    private JsonMap? SelectedOmLine()
    {
        if (OmLinesGrid.SelectedItem is not FbsLineRow row || _omJob is null) return null;
        return _omJob.Arr("lines").FirstOrDefault(line => line.Int("id") == row.Id);
    }

    private void OnOmLinesContextOpening(object sender, ContextMenuEventArgs e)
    {
        var line = SelectedOmLine();
        if (line is null || OmLinesGrid.ContextMenu?.Items.Count is not > 0)
        {
            e.Handled = true;
            return;
        }
        if (OmLinesGrid.ContextMenu.Items[0] is MenuItem item)
            item.Header = line.Str("status") == "done" ? "Статус: в сборке" : "Статус: готово";
    }

    private async void OnOmLineStatusToggle(object sender, RoutedEventArgs e)
    {
        var line = SelectedOmLine();
        if (line is null || _omJob is null) return;
        var lineId = line.Int("id");
        var next = line.Str("status") == "done" ? "pending" : "done";
        try
        {
            var payload = await _client.OtherMpSetLineStatusAsync(_omJob.Int("id"), lineId, next);
            _omJob = payload.Obj("job") ?? _omJob;
            RenderOmJob();
            SetStatus($"Статус строки: {Paging.LineStatusRu(next)}");
            OmScanBox.Focus();
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
        }
    }

    private async void OnOmPrintA4(object sender, RoutedEventArgs e)
    {
        if (_omJob is null)
        {
            MessageBox.Show(this, "Выберите задание", "Печать А4");
            return;
        }
        var jobId = _omJob.Int("id");
        try
        {
            _omJob = await _client.OtherMpOpenJobAsync(jobId);
            RenderOmJob();
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
            return;
        }
        var purchase = _omJob?.Str("purchase_status") ?? "";
        if (purchase.Length == 0)
        {
            MessageBox.Show(this, "Менеджер не указал статус закупки", "Печать А4");
            return;
        }
        var dialog = new Window
        {
            Title = "Печать А4",
            Width = 360,
            Height = 230,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
        };
        var type = new ComboBox { Margin = new Thickness(0, 0, 0, 8) };
        type.Items.Add(new ComboBoxItem { Content = "Паллеты", Tag = "pallets" });
        type.Items.Add(new ComboBoxItem { Content = "Короба", Tag = "boxes" });
        type.SelectedIndex = 0;
        var count = new TextBox { Text = "1", Margin = new Thickness(0, 0, 0, 12) };
        var ok = new Button { Content = "Печать", IsDefault = true, Width = 100, HorizontalAlignment = HorizontalAlignment.Left };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = $"Статус закупки: {purchase}", Margin = new Thickness(0, 0, 0, 8) });
        panel.Children.Add(new TextBlock { Text = "Тип грузомест" });
        panel.Children.Add(type);
        panel.Children.Add(new TextBlock { Text = "Количество" });
        panel.Children.Add(count);
        panel.Children.Add(ok);
        dialog.Content = panel;
        string? cargo = null;
        var copies = 1;
        ok.Click += (_, _) =>
        {
            if (!int.TryParse(count.Text.Trim(), out copies) || copies < 1)
            {
                MessageBox.Show(dialog, "Укажите количество от 1", "Печать А4");
                return;
            }
            cargo = (type.SelectedItem as ComboBoxItem)?.Tag as string ?? "pallets";
            dialog.DialogResult = true;
        };
        if (dialog.ShowDialog() != true || cargo is null) return;
        await RunPrint("Печать А4...", async () =>
        {
            var pdf = await _client.OtherMpRouteSheetAsync(jobId, cargo, copies);
            await Task.Run(() => GdiPrinter.PrintPdf(pdf, _config.A4Profile()));
        });
        SetStatus($"А4: {purchase}, {copies} {(cargo == "boxes" ? "коробов" : "паллет")}");
    }
}
