using System.Windows;
using WarehousePacking.Services;

namespace WarehousePacking.Views;

public partial class LoginWindow : Window
{
    public AppConfig Config { get; private set; } = AppConfig.Load();
    public ApiClient? Client { get; private set; }
    public string UserName { get; private set; } = "";
    public int UserId { get; private set; }

    public LoginWindow()
    {
        InitializeComponent();
        ServerBox.Text = Config.ServerUrl;
        ApiBox.Text = Config.ApiUrl;
        Loaded += async (_, _) => await LoadLoginUsersAsync();
        LoginBox.Focus();
    }

    private async Task LoadLoginUsersAsync()
    {
        var typedLogin = LoginBox.Text.Trim();
        try
        {
            using var client = new ApiClient(
                ServerBox.Text.Trim().TrimEnd('/'),
                ApiBox.Text.Trim().TrimEnd('/'));
            var users = await client.GetLoginUsersAsync();
            LoginBox.ItemsSource = users
                .Select(user => new LoginUserOption(
                    user.Str("login"),
                    user.Str("display_name", user.Str("login"))))
                .Where(user => user.Login.Length > 0)
                .ToList();
            LoginBox.Text = typedLogin;
            StatusText.Text = users.Count > 0
                ? "Выберите сотрудника и введите пароль"
                : "Активные сотрудники не найдены";
        }
        catch
        {
            LoginBox.Text = typedLogin;
            StatusText.Text = "Список сотрудников недоступен, логин можно ввести вручную";
        }
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
            _ = LoadLoginUsersAsync();
        }
    }

    private void OnLoginSelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (LoginBox.SelectedItem is LoginUserOption)
            PasswordBox.Focus();
    }

    private async void OnLogin(object sender, RoutedEventArgs e)
    {
        var server = ServerBox.Text.Trim().TrimEnd('/');
        var api = ApiBox.Text.Trim().TrimEnd('/');
        var login = LoginBox.SelectedItem is LoginUserOption selected
            ? selected.Login
            : LoginBox.Text.Trim();
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
            UserId = user?.Int("id") ?? 0;
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

public sealed record LoginUserOption(string Login, string DisplayName);
