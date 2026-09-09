using System.Windows;
using WarehousePacking.Services;

namespace WarehousePacking.Views;

public partial class SettingsWindow : Window
{
    private readonly AppConfig _config;
    private readonly ApiClient? _client;
    public Action? CacheHintChanged { get; set; }

    public SettingsWindow(AppConfig config, ApiClient? client)
    {
        InitializeComponent();
        _config = config;
        _client = client;
        var printers = GdiPrinter.InstalledPrinters().ToList();
        printers.Insert(0, "");
        PrinterA4Box.ItemsSource = printers;
        PrinterLabelBox.ItemsSource = printers;
        ServerBox.Text = config.ServerUrl;
        ApiBox.Text = config.ApiUrl;
        PrinterA4Box.Text = config.PrinterA4;
        SettingsA4Box.Text = config.PrintSettingsA4;
        PrinterLabelBox.Text = config.LabelPrinter;
        SettingsLabelBox.Text = config.LabelSettings;
        RefreshBox.Text = config.RefreshSeconds;
        RefreshCacheSummary();
    }

    private void RefreshCacheSummary()
    {
        var s = LabelCache.Summary();
        CacheSummary.Text = $"На диске: {s.Jobs} заданий, {s.Files} ярлыков, {LabelCache.FormatSize(s.Bytes)}.";
    }

    private async void OnClearCache(object sender, RoutedEventArgs e)
    {
        if (_client is null || !_client.ApiOk)
        {
            MessageBox.Show(this,
                "Войдите в приложение и подключитесь к API, чтобы удалить кэш только у выполненных заданий.",
                "Кэш ярлыков");
            return;
        }
        if (MessageBox.Show(this,
                "Удалить скачанные ярлыки по выполненным и отменённым заданиям?\nЗадания в работе останутся.",
                "Очистить кэш", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        try
        {
            var jobs = await _client.FbsMyJobsAsync();
            var keep = jobs.Select(j => j.IntOrNull("id")).Where(id => id is not null).Select(id => id!.Value).ToHashSet();
            var result = LabelCache.ClearExcept(keep);
            RefreshCacheSummary();
            CacheHintChanged?.Invoke();
            if (result.Jobs <= 0)
            {
                MessageBox.Show(this, "Нечего удалять: кэш выполненных заданий пуст.", "Кэш ярлыков");
                return;
            }
            MessageBox.Show(this, $"Удалено заданий: {result.Jobs} ({LabelCache.FormatSize(result.Bytes)}).", "Кэш ярлыков");
        }
        catch (AuthException ex)
        {
            MessageBox.Show(this, ex.Message, "Сессия", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(RefreshBox.Text.Trim(), out var seconds) || seconds < 5)
        {
            MessageBox.Show(this, "Интервал автообновления должен быть числом не меньше 5", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        _config.ServerUrl = ServerBox.Text.Trim();
        _config.ApiUrl = ApiBox.Text.Trim();
        _config.PrinterA4 = PrinterA4Box.Text.Trim();
        _config.PrintSettingsA4 = SettingsA4Box.Text.Trim();
        _config.PrinterLabel = PrinterLabelBox.Text.Trim();
        _config.PrintSettingsLabel = SettingsLabelBox.Text.Trim();
        _config.Printer = _config.PrinterLabel;
        _config.PrintSettings = _config.PrintSettingsLabel;
        _config.RefreshSeconds = seconds.ToString();
        try
        {
            _config.Save();
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Ошибка сохранения", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
