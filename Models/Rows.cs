using System.Windows.Media;

namespace WarehousePacking.Models;

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

public sealed class CatalogRow
{
    public int Id { get; init; }
    public ImageSource? Photo { get; set; }
    public string Sku { get; init; } = "";
    public string Name { get; init; } = "";
}

public sealed class FbsJobRow
{
    public int Id { get; init; }
    public string Status { get; init; } = "";
    public string Progress { get; init; } = "";
}

public sealed class FbsLineRow
{
    public int Id { get; init; }
    public string Seq { get; init; } = "";
    public ImageSource? Photo { get; set; }
    public string Sku { get; init; } = "";
    public string Name { get; init; } = "";
    public string Order { get; init; } = "";
    public string Status { get; init; } = "";
    public string Tip { get; init; } = "";
}

public sealed class RemainingRow
{
    public int Index { get; init; }
    public ImageSource? Photo { get; set; }
    public string Sku { get; init; } = "";
    public string Name { get; init; } = "";
    public string Qty { get; init; } = "";
    public string Barcode { get; init; } = "";
}
