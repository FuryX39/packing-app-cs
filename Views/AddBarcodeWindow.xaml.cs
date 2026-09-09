using System.Windows;

namespace WarehousePacking.Views;

public partial class AddBarcodeWindow : Window
{
    public string LabelText => LabelBox.Text.Trim();
    public string GroupText => GroupBox.Text.Trim();
    public string BarcodeText => BarcodeBox.Text.Trim();

    public AddBarcodeWindow(string sku, string name)
    {
        InitializeComponent();
        ProductText.Text = $"{sku}  {name}";
        BarcodeBox.Focus();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (BarcodeText.Length == 0)
        {
            MessageBox.Show(this, "Введите штрихкод", "Штрихкод", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
        Close();
    }
}
