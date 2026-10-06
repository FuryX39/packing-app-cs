using System.Windows;
using System.Windows.Input;

namespace WarehousePacking.Views;

public partial class PasswordPromptWindow : Window
{
    public string Password => PasswordInput.Password;

    public PasswordPromptWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => PasswordInput.Focus();
    }

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        if (PasswordInput.Password.Length == 0)
        {
            MessageBox.Show(this, "Введите пароль.", "Моя выработка",
                MessageBoxButton.OK, MessageBoxImage.Information);
            PasswordInput.Focus();
            return;
        }
        DialogResult = true;
    }

    private void OnPasswordKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            OnOpen(sender, e);
    }
}
