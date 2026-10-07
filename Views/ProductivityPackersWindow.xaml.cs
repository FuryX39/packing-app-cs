using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;

namespace WarehousePacking.Views;

public partial class ProductivityPackersWindow : Window
{
    public ObservableCollection<PackerChoice> Packers { get; } = [];

    public IReadOnlyList<int> SelectedIds =>
        Packers.Where(item => item.IsChecked).Select(item => item.Id).ToList();

    public ProductivityPackersWindow(
        string taskTitle,
        IEnumerable<PackerChoice> packers)
    {
        InitializeComponent();
        TaskInfo.Text = taskTitle;
        foreach (var packer in packers)
            Packers.Add(packer);
        PackersList.ItemsSource = Packers;
        UpdateToggleText();
    }

    private void OnToggleDrop(object sender, RoutedEventArgs e)
    {
        DropPopup.IsOpen = DropToggle.IsChecked == true;
        if (DropPopup.IsOpen)
            DropPopup.Closed += OnPopupClosed;
    }

    private void OnPopupClosed(object? sender, EventArgs e)
    {
        DropPopup.Closed -= OnPopupClosed;
        DropToggle.IsChecked = false;
        UpdateToggleText();
    }

    private void OnPackerChecked(object sender, RoutedEventArgs e) => UpdateToggleText();

    private void UpdateToggleText()
    {
        var names = Packers.Where(item => item.IsChecked).Select(item => item.Name).ToList();
        DropToggle.Content = names.Count switch
        {
            0 => "Не выбрано",
            1 => names[0],
            _ => $"{names[0]} и ещё {names.Count - 1}",
        };
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}

public sealed class PackerChoice : INotifyPropertyChanged
{
    private bool _isChecked;

    public int Id { get; init; }
    public string Name { get; init; } = "";

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value)
                return;
            _isChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
