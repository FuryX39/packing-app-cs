using System.Windows;

namespace WarehousePacking.Views;

public partial class AddGtinWindow : Window
{
    public string CodeText => CodeBox.Text.Trim();

    public AddGtinWindow(string sku, string name)
    {
        InitializeComponent();
        ProductText.Text = $"{sku}  {name}";
        CodeBox.Focus();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (CodeText.Length == 0)
        {
            MessageBox.Show(this, "Отсканируйте маркировку Честный знак", "GTIN", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
        Close();
    }
}
