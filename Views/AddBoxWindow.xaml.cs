using System.Windows;
using System.Windows.Input;

namespace WarehousePacking.Views;

public partial class AddBoxWindow : Window
{
    public string QtyText => QtyBox.Text.Trim();
    public string BarcodeText => BarcodeBox.Text.Trim();
    public int Quantity { get; private set; }

    public AddBoxWindow(string sku, string name)
    {
        InitializeComponent();
        ProductText.Text = $"{sku}  {name}";
        QtyBox.Focus();
    }

    private void OnQtyKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        BarcodeBox.Focus();
        BarcodeBox.SelectAll();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(QtyText, out var qty) || qty < 1)
        {
            MessageBox.Show(this, "Укажите количество товаров в коробе", "ШК короба", MessageBoxButton.OK, MessageBoxImage.Warning);
            QtyBox.Focus();
            QtyBox.SelectAll();
            return;
        }
        if (BarcodeText.Length == 0)
        {
            MessageBox.Show(this, "Отсканируйте или введите штрихкод короба", "ШК короба", MessageBoxButton.OK, MessageBoxImage.Warning);
            BarcodeBox.Focus();
            return;
        }
        Quantity = qty;
        DialogResult = true;
        Close();
    }
}
