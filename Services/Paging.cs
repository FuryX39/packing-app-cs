namespace WarehousePacking.Services;

public static class Paging
{
    public const int PageSize = 50;

    public static int PageCount(int total, int pageSize = PageSize) =>
        total <= 0 ? 1 : (Math.Max(0, total) + Math.Max(1, pageSize) - 1) / Math.Max(1, pageSize);

    public static int Clamp(int page, int total, int pageSize = PageSize) =>
        Math.Max(0, Math.Min(page, PageCount(total, pageSize) - 1));

    public static (List<T> Items, int Page) Slice<T>(IReadOnlyList<T> items, int page, int pageSize = PageSize)
    {
        var total = items.Count;
        page = Clamp(page, total, pageSize);
        var start = page * pageSize;
        return (items.Skip(start).Take(pageSize).ToList(), page);
    }

    public static string RangeLabel(int total, int page, int pageSize = PageSize)
    {
        if (total <= 0) return "0 из 0";
        page = Clamp(page, total, pageSize);
        var start = page * pageSize + 1;
        var end = Math.Min(total, (page + 1) * pageSize);
        return $"{start}–{end} из {total}";
    }

    public static bool TextMatches(string query, params object?[] fields)
    {
        var needle = (query ?? "").Trim().ToLowerInvariant();
        if (needle.Length == 0) return true;
        return fields.Any(f => (f?.ToString() ?? "").ToLowerInvariant().Contains(needle));
    }

    public static IEnumerable<string> BarcodeTexts(JsonMap product)
    {
        foreach (var item in product.Arr("barcodes"))
        {
            var code = item.Str("barcode");
            if (code.Length > 0) yield return code;
        }
    }

    public static bool CatalogMatches(JsonMap product, string query) =>
        TextMatches(query, product.Str("name"), product.Str("sku"), product.Str("code"),
            product.Str("external_code"), product.Str("group_name"), string.Join(' ', BarcodeTexts(product)));

    public static bool JobLineMatches(JsonMap line, string query, string extra = "") =>
        TextMatches(query, line.Str("sku"), line.Str("product_name"), line.Str("name"),
            line.Str("order_id"), line.Str("order_display"), line.Str("barcode"), extra);

    public static bool RemainingMatches(JsonMap group, string query) =>
        TextMatches(query, group.Str("sku"), group.Str("name"), group.Str("barcode"));

    public static string FormatPicked(IReadOnlyList<JsonMap> lines, bool skipMp)
    {
        if (lines.Count == 0) return "";
        var first = lines[0];
        var cis = first.Flag("has_cis") ? " · КИЗ" : "";
        var sku = first.Str("sku");
        var name = first.Str("product_name", first.Str("name"));
        if (lines.Count == 1)
            return $"SKU {sku} · {name} · заказ {first.Str("order_display", first.Str("order_id"))} · строка #{first.Str("id")}{cis}";
        var orders = string.Join(", ", lines.Select(x => x.Str("order_display", x.Str("order_id", "?"))));
        var confirm = skipMp ? "" : " · пропикайте ярлыки подряд";
        return $"SKU {sku} · {name} · {lines.Count} шт. · заказы: {orders}{confirm}{cis}";
    }

    public static string JobStatusRu(string raw) => raw switch
    {
        "open" => "Открыто",
        "in_progress" => "В работе",
        "done" => "Готово",
        "cancelled" => "Отменено",
        _ => string.IsNullOrWhiteSpace(raw) ? "—" : raw,
    };

    public static string LineStatusRu(string raw) => raw switch
    {
        "pending" => "В сборке",
        "printed" => "Печать",
        "done" => "Готово",
        _ => string.IsNullOrWhiteSpace(raw) ? "—" : raw,
    };

    public static string FormatDay(string value)
    {
        var raw = (value ?? "").Trim();
        var parts = raw.Split('-');
        return parts.Length == 3 ? $"{parts[2]}.{parts[1]}.{parts[0]}" : (raw.Length == 0 ? "—" : raw);
    }

    public static string ShipTag(string endDate)
    {
        var raw = (endDate ?? "").Trim().Split('-');
        if (raw.Length != 3) return "";
        if (!int.TryParse(raw[0], out var y) || !int.TryParse(raw[1], out var m) || !int.TryParse(raw[2], out var d))
            return "";
        try
        {
            var ship = new DateOnly(y, m, d);
            var today = DateOnly.FromDateTime(DateTime.Today);
            if (ship == today) return "today";
            if (ship == today.AddDays(1)) return "tomorrow";
        }
        catch { }
        return "";
    }

    public static List<Dictionary<string, string>> NormalizeBarcodes(JsonMap? details)
    {
        var outList = new List<Dictionary<string, string>>();
        if (details is null) return outList;
        foreach (var item in details.Arr("barcodes"))
        {
            var code = item.Str("barcode").Trim();
            if (code.Length == 0) continue;
            outList.Add(new Dictionary<string, string>
            {
                ["barcode"] = code,
                ["label"] = item.Str("label"),
                ["group"] = item.Str("group"),
            });
        }
        return outList;
    }

    public static string BarcodePickTitle(Dictionary<string, string> item)
    {
        if (item.TryGetValue("label", out var label) && label.Trim().Length > 0) return label.Trim();
        if (item.TryGetValue("group", out var group) && group.Trim().Length > 0) return group.Trim();
        return item.GetValueOrDefault("barcode") ?? "";
    }

    public static string BarcodeComboLabel(Dictionary<string, string> item)
    {
        var title = BarcodePickTitle(item);
        var code = item.GetValueOrDefault("barcode") ?? "";
        return title.Length > 0 && code.Length > 0 && title != code ? $"{title} ({code})" : (title.Length > 0 ? title : code);
    }
}
