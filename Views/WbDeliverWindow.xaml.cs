using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using WarehousePacking.Services;

namespace WarehousePacking.Views;

public partial class WbDeliverWindow : Window
{
    public int PointId { get; private set; }
    public string PointKey { get; private set; } = "";
    public string ShippingDate { get; private set; } = "";
    public string ShippingType { get; private set; } = "";
    public string WaybillUuid { get; private set; } = "";

    public WbDeliverWindow(JsonMap meta)
    {
        InitializeComponent();
        var supplies = meta.StrList("supply_ids");
        SuppliesText.Text = supplies.Count > 0
            ? "Поставки: " + string.Join(", ", supplies)
            : "Поставки не указаны";
        var defaultKey = meta.Str("default_point_key");
        var points = meta.Arr("points");
        var selected = false;
        foreach (var point in points)
        {
            var key = point.Str("key");
            var available = point.Flag("available") && point.Int("shipping_point_id") > 0;
            var radio = new RadioButton
            {
                GroupName = "WbDeliverPoint",
                Content = point.Str("title"),
                Tag = point,
                IsEnabled = available,
                Margin = new Thickness(0, 2, 0, 2),
            };
            if (available && !selected && (key == defaultKey || defaultKey.Length == 0))
            {
                radio.IsChecked = true;
                selected = true;
            }
            PointsHost.Children.Add(radio);
        }
        if (!selected)
        {
            foreach (RadioButton radio in PointsHost.Children)
            {
                if (radio.IsEnabled)
                {
                    radio.IsChecked = true;
                    break;
                }
            }
        }
        DateBox.SelectedDate = null;
        TypeSelf.IsChecked = false;
        TypeTk.IsChecked = false;
    }

    private void OnTypeChanged(object sender, RoutedEventArgs e)
    {
        TrnHost.Visibility = TypeTk.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        RadioButton? chosen = null;
        foreach (RadioButton radio in PointsHost.Children)
        {
            if (radio.IsChecked == true)
            {
                chosen = radio;
                break;
            }
        }
        if (chosen?.Tag is not JsonMap point || point.Int("shipping_point_id") <= 0)
        {
            MessageBox.Show(this, "Выберите пункт сдачи", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (DateBox.SelectedDate is not DateTime date)
        {
            MessageBox.Show(this, "Укажите дату сдачи", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            DateBox.Focus();
            return;
        }
        if (TypeSelf.IsChecked != true && TypeTk.IsChecked != true)
        {
            MessageBox.Show(this, "Выберите способ: самостоятельно или транспортной компанией", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var waybill = TrnBox.Text.Trim();
        if (TypeTk.IsChecked == true && waybill.Length == 0)
        {
            MessageBox.Show(this, "Для транспортной компании укажите УИД ТРН", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            TrnBox.Focus();
            return;
        }
        PointId = point.Int("shipping_point_id");
        PointKey = point.Str("key");
        ShippingDate = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        ShippingType = TypeTk.IsChecked == true ? "transportCompany" : "selfShipping";
        WaybillUuid = waybill;
        DialogResult = true;
        Close();
    }
}
