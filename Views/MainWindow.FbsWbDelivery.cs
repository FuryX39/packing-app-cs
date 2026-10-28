using System.Windows;
using WarehousePacking.Services;

namespace WarehousePacking.Views;

public partial class MainWindow
{
    private async void OnFbsWbDeliver(object sender, RoutedEventArgs e)
    {
        if (_fbsJob is null)
        {
            MessageBox.Show(this, "Сначала откройте задание", "FBS");
            return;
        }
        var jobId = _fbsJob.Int("id");
        await FbsRunAsync(async () =>
        {
            var meta = await _client.FbsWbDeliveryMetaAsync(jobId);
            var dialog = new WbDeliverWindow(meta) { Owner = this };
            if (dialog.ShowDialog() != true)
            {
                SetStatus("Передача в доставку отменена");
                return;
            }
            var pdf = await _client.FbsWbDeliverAsync(
                jobId,
                dialog.PointId,
                dialog.PointKey,
                dialog.ShippingDate,
                dialog.ShippingType,
                dialog.WaybillUuid);
            await Task.Run(() => GdiPrinter.PrintPdfsIsolated([pdf], _config.LabelProfile()));
            _fbsJob = await _client.FbsOpenJobAsync(jobId);
            await LoadFbsJobsAsync();
            RenderFbsJob();
            SetStatus("Поставки переданы в доставку, QR отправлен на печать");
            FocusScan();
        }, "Передача в доставку...");
    }

    private async void OnFbsWbPrintQr(object sender, RoutedEventArgs e)
    {
        if (_fbsJob is null)
        {
            MessageBox.Show(this, "Сначала откройте задание", "FBS");
            return;
        }
        await FbsRunAsync(async () =>
        {
            var pdf = await _client.FbsWbSupplyQrAsync(_fbsJob.Int("id"));
            await Task.Run(() => GdiPrinter.PrintPdfsIsolated([pdf], _config.LabelProfile()));
            SetStatus("QR поставок отправлен на печать");
            FocusScan();
        }, "Печать QR поставок...");
    }
}
