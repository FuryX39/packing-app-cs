using System.Text.Json;

namespace WarehousePacking.Services;

public sealed class JsonMap
{
    public JsonElement El { get; }

    public JsonMap(JsonElement el) => El = el;

    public static JsonMap Parse(string json) => new(JsonDocument.Parse(json).RootElement.Clone());

    public static JsonMap? TryParse(string json)
    {
        try { return Parse(json); }
        catch { return null; }
    }

    public bool Has(string name) => El.ValueKind == JsonValueKind.Object && El.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null && v.ValueKind != JsonValueKind.Undefined;

    public JsonElement? Prop(string name)
    {
        if (El.ValueKind != JsonValueKind.Object || !El.TryGetProperty(name, out var v))
            return null;
        if (v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        return v;
    }

    public string Str(string name, string fallback = "")
    {
        var p = Prop(name);
        if (p is null) return fallback;
        return p.Value.ValueKind switch
        {
            JsonValueKind.String => p.Value.GetString() ?? fallback,
            JsonValueKind.Number => p.Value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => fallback,
        };
    }

    public int Int(string name, int fallback = 0)
    {
        var p = Prop(name);
        if (p is null) return fallback;
        if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var n))
            return n;
        return int.TryParse(p.Value.ToString(), out var parsed) ? parsed : fallback;
    }

    public int? IntOrNull(string name)
    {
        var p = Prop(name);
        if (p is null) return null;
        if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var n))
            return n;
        return int.TryParse(Str(name), out var parsed) ? parsed : null;
    }

    public bool Flag(string name) =>
        Prop(name) is { } p && (
            p.ValueKind == JsonValueKind.True ||
            (p.ValueKind == JsonValueKind.String && p.GetString() is "1" or "true" or "True"));

    public JsonMap? Obj(string name)
    {
        var p = Prop(name);
        return p is { ValueKind: JsonValueKind.Object } ? new JsonMap(p.Value) : null;
    }

    public List<JsonMap> Arr(string name)
    {
        var p = Prop(name);
        if (p is null || p.Value.ValueKind != JsonValueKind.Array)
            return [];
        return p.Value.EnumerateArray().Select(x => new JsonMap(x)).ToList();
    }

    public List<string> StrList(string name)
    {
        var p = Prop(name);
        if (p is null || p.Value.ValueKind != JsonValueKind.Array)
            return [];
        return p.Value.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.ToString()).Where(s => s.Length > 0).ToList();
    }
}

public sealed class AuthException : Exception
{
    public AuthException(string message) : base(message) { }
}

public sealed class ApiException : Exception
{
    public int StatusCode { get; }
    public ApiException(string message, int statusCode = 0) : base(message) => StatusCode = statusCode;
    public ApiException(string message, Exception? inner, int statusCode = 0)
        : base(message, inner) => StatusCode = statusCode;
}
