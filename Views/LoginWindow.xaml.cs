using System.Windows;
using WarehousePacking.Services;

namespace WarehousePacking.Views;

public partial class LoginWindow : Window
{
    public AppConfig Config { get; private set; } = AppConfig.Load();
    public ApiClient? Client { get; private set; }
    public string UserName { get; private set; } = "";

    public LoginWindow()
    {
        InitializeComponent();
        ServerBox.Text = Config.ServerUrl;
        ApiBox.Text = Config.ApiUrl;
        LoginBox.Focus();
    }

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow(Config, null) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            Config = AppConfig.Load();
            ServerBox.Text = Config.ServerUrl;
            ApiBox.Text = Config.ApiUrl;
            StatusText.Text = "Настройки сохранены";
        }
    }

    private async void OnLogin(object sender, RoutedEventArgs e)
    {
        var server = ServerBox.Text.Trim().TrimEnd('/');
        var api = ApiBox.Text.Trim().TrimEnd('/');
        var login = LoginBox.Text.Trim();
        var password = PasswordBox.Password;
        if (server.Length == 0)
        {
            MessageBox.Show("Укажите адрес сервера", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (login.Length == 0 || password.Length == 0)
        {
            MessageBox.Show("Введите логин и пароль", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (server != Config.ServerUrl || api != Config.ApiUrl)
        {
            Config.ServerUrl = server;
            Config.ApiUrl = api;
            Config.Save();
            Config = AppConfig.Load();
        }
        StatusText.Text = "Вход...";
        IsEnabled = false;
        try
        {
            var client = new ApiClient(server, api);
            var session = await client.LoginAsync(login, password);
            if (!client.ApiOk)
            {
                MessageBox.Show(
                    (client.ApiError.Length > 0 ? client.ApiError : "Нет сессии API") +
                    "\n\nЗадания и номенклатура работают. Вкладка «Упаковка FBS» нужна после запуска run_api.py.",
                    "API упаковщиков",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            var user = session.Obj("user");
            UserName = user?.Str("display_name", user.Str("login", login)) ?? login;
            Client = client;
            DialogResult = true;
            Close();
        }
        catch (AuthException ex)
        {
            MessageBox.Show(ex.Message, "Ошибка входа", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Не удалось подключиться к серверу:\n{ex.Message}", "Ошибка входа", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText.Text = "";
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
