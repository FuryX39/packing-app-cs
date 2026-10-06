using System.Collections.ObjectModel;
using System.Windows;
using WarehousePacking.Services;

namespace WarehousePacking.Views;

public partial class MainWindow
{
    private const int ProductivityTabIndex = 6;
    private readonly ObservableCollection<ProductivityDayRow> _productivityDays = [];
    private bool _productivityTabTransition;
    private int _lastRegularTabIndex;
    private int _productivityLoadVersion;
    private string _productivityPassword = "";
    private DateTime _productivityMonth =
        new(DateTime.Today.Year, DateTime.Today.Month, 1);

    private void InitializeProductivity()
    {
        ProductivityDays.ItemsSource = _productivityDays;
        ProductivityMonthText.Text = _productivityMonth.ToString("MMMM yyyy");
    }

    private bool HandleProductivityTabChange()
    {
        if (_productivityTabTransition)
            return true;
        if (Tabs.SelectedIndex == ProductivityTabIndex)
        {
            _tasksTimer.Stop();
            _ = EnterProductivityAsync();
            return true;
        }

        LockProductivity();
        _lastRegularTabIndex = Tabs.SelectedIndex;
        return false;
    }

    private async Task EnterProductivityAsync()
    {
        var prompt = new PasswordPromptWindow { Owner = this };
        if (prompt.ShowDialog() != true)
        {
            ReturnFromProductivity();
            return;
        }

        _productivityPassword = prompt.Password;
        try
        {
            await LoadProductivityAsync();
        }
        catch (Exception exc)
        {
            if (Tabs.SelectedIndex != ProductivityTabIndex)
                return;
            var message = exc is ApiException { StatusCode: 401 }
                ? "Неверный пароль текущего аккаунта."
                : exc.Message;
            MessageBox.Show(this, message, "Моя выработка",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            ReturnFromProductivity();
        }
    }

    private async Task LoadProductivityAsync()
    {
        if (_productivityPassword.Length == 0)
            return;

        var loadVersion = ++_productivityLoadVersion;
        SetStatus("Загрузка выработки...");
        var data = await _client.GetMyProductivityAsync(
            _productivityPassword,
            _productivityMonth.Year,
            _productivityMonth.Month);
        if (loadVersion != _productivityLoadVersion ||
            Tabs.SelectedIndex != ProductivityTabIndex)
            return;

        _productivityDays.Clear();
        foreach (var day in data.Arr("days"))
        {
            var tasks = day.Arr("tasks")
                .Select(task => new ProductivityTaskRow(
                    task.Str("task_type_name"),
                    task.Str("task_data"),
                    task.Int("quantity")))
                .ToList();
            _productivityDays.Add(new ProductivityDayRow(
                day.Str("display_date"),
                day.Int("quantity"),
                tasks));
        }

        ProductivityMonthText.Text = _productivityMonth.ToString("MMMM yyyy");
        ProductivityTotalText.Text = $"Всего: {data.Int("total_quantity")} шт.";
        ProductivityEmptyText.Visibility =
            _productivityDays.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SetStatus("Выработка загружена");
    }

    private void LockProductivity()
    {
        _productivityLoadVersion++;
        _productivityPassword = "";
        _productivityDays.Clear();
        if (IsInitialized)
        {
            ProductivityTotalText.Text = "";
            ProductivityEmptyText.Visibility = Visibility.Collapsed;
        }
    }

    private void ReturnFromProductivity()
    {
        LockProductivity();
        _productivityTabTransition = true;
        Tabs.SelectedIndex = Math.Max(0, _lastRegularTabIndex);
        _productivityTabTransition = false;
    }

    private async void OnProductivityReload(object sender, RoutedEventArgs e)
    {
        try
        {
            await LoadProductivityAsync();
        }
        catch (Exception exc)
        {
            if (Tabs.SelectedIndex != ProductivityTabIndex)
                return;
            MessageBox.Show(this, exc.Message, "Моя выработка",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnProductivityPreviousMonth(object sender, RoutedEventArgs e)
    {
        _productivityMonth = _productivityMonth.AddMonths(-1);
        ProductivityMonthText.Text = _productivityMonth.ToString("MMMM yyyy");
        OnProductivityReload(sender, e);
    }

    private void OnProductivityNextMonth(object sender, RoutedEventArgs e)
    {
        _productivityMonth = _productivityMonth.AddMonths(1);
        ProductivityMonthText.Text = _productivityMonth.ToString("MMMM yyyy");
        OnProductivityReload(sender, e);
    }
}

public sealed record ProductivityTaskRow(
    string TaskType,
    string TaskData,
    int Quantity);

public sealed record ProductivityDayRow(
    string Date,
    int Quantity,
    IReadOnlyList<ProductivityTaskRow> Tasks);
