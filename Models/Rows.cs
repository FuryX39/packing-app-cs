using System.ComponentModel;
using System.Windows.Media;

namespace WarehousePacking.Models;

/// Row whose thumbnail arrives later, so the grid updates the cell in place
/// instead of being rebuilt from scratch.
public abstract class PhotoRow : INotifyPropertyChanged
{
    private static readonly PropertyChangedEventArgs PhotoArgs = new(nameof(Photo));
    private ImageSource? _photo;

    public ImageSource? Photo
    {
        get => _photo;
        set
        {
            if (ReferenceEquals(_photo, value))
                return;
            _photo = value;
            PropertyChanged?.Invoke(this, PhotoArgs);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class TaskRow
{
    public int Id { get; init; }
    public string Assembly { get; init; } = "";
    public string Marketplace { get; init; } = "";
    public string Ship { get; init; } = "";
    public string Tag { get; init; } = "";
}

public sealed class AttachmentRow
{
    public int Id { get; init; }
    public string Kind { get; init; } = "";
    public string Filename { get; init; } = "";
}

public sealed class CatalogRow : PhotoRow
{
    public int Id { get; init; }
    public string Sku { get; init; } = "";
    public string Name { get; init; } = "";
}

public sealed class FbsJobRow
{
    public int Id { get; init; }
    public string Status { get; init; } = "";
    public string Progress { get; init; } = "";
}

public sealed class FbsLineRow : PhotoRow
{
    public int Id { get; init; }
    public string Seq { get; init; } = "";
    public string Sku { get; init; } = "";
    public string Name { get; init; } = "";
    public string Order { get; init; } = "";
    public string Status { get; init; } = "";
}

public sealed class RemainingRow : PhotoRow
{
    public int Index { get; init; }
    public string Sku { get; init; } = "";
    public string Name { get; init; } = "";
    public string Qty { get; init; } = "";
    public string Barcode { get; init; } = "";
}

public sealed class FboOverviewLineRow
{
    public string Text { get; init; } = "";
    public int BoxId { get; init; }
    public string BoxCode { get; init; } = "";
    public string ProductBarcode { get; init; } = "";
    public bool CanUnassign { get; init; }
}

public sealed class FboOverviewGroupRow
{
    public string Title { get; init; } = "";
    public IReadOnlyList<FboOverviewLineRow> Lines { get; init; } = [];
    public bool IsEmpty => Lines.Count == 0;
    public string EmptyText { get; init; } = "Пусто";
    public int BoxId { get; init; }
    public string BoxCode { get; init; } = "";
    public string ProductBarcode { get; init; } = "";
    public bool CanUnassign { get; init; }
}
