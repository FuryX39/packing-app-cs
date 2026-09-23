using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace WarehousePacking.Services;

public sealed class ApiClient : IDisposable
{
    private readonly CookieContainer _webCookies = new();
    private readonly CookieContainer _apiCookies = new();
    private readonly HttpClient _web;
    private readonly HttpClient _apiHttp;

    public string ServerUrl { get; }
    public string ApiUrl { get; }
    public bool ApiOk { get; private set; }
    public string ApiError { get; private set; } = "";

    public ApiClient(string serverUrl, string apiUrl = "")
    {
        ServerUrl = serverUrl.TrimEnd('/');
        ApiUrl = DeriveApiUrl(ServerUrl, apiUrl);
        _web = NewClient(_webCookies);
        _apiHttp = NewClient(_apiCookies);
    }

    public static string DeriveApiUrl(string serverUrl, string apiUrl)
    {
        var explicitUrl = (apiUrl ?? "").Trim().TrimEnd('/');
        if (explicitUrl.Length > 0)
            return explicitUrl;
        var raw = (serverUrl ?? "").Trim().TrimEnd('/');
        if (raw.Length == 0)
            return "";
        if (!raw.Contains("://"))
            raw = "http://" + raw;
        var uri = new Uri(raw);
        var builder = new UriBuilder(uri) { Port = 8766, Path = "", Query = "", Fragment = "" };
        return builder.Uri.ToString().TrimEnd('/');
    }

    public async Task<JsonMap> LoginAsync(string login, string password, CancellationToken ct = default)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["login"] = login.Trim(),
            ["password"] = password,
        });
        using var resp = await _web.PostAsync(Web("/api/warehouse/login"), form, ct);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            throw new AuthException("Неверный логин или пароль");
        if (resp.StatusCode == HttpStatusCode.BadRequest)
            throw new AuthException(await DetailAsync(resp) ?? "Введите логин и пароль");
        if (!resp.IsSuccessStatusCode)
            throw new AuthException(await DetailAsync(resp) ?? resp.ReasonPhrase ?? "Ошибка входа");
        var session = await GetSessionAsync(ct);
        await TryLoginApiAsync(login, password, ct);
        return session;
    }

    public async Task LogoutAsync()
    {
        try { await _web.PostAsync(Web("/api/warehouse/logout"), null); } catch { }
        try
        {
            if (ApiUrl.Length > 0)
                await _apiHttp.PostAsync(Api("/api/v1/logout"), null);
        }
        catch { }
        ApiOk = false;
    }

    public async Task<JsonMap> GetSessionAsync(CancellationToken ct = default)
    {
        using var resp = await _web.GetAsync(Web("/api/warehouse/session"), ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        Raise(resp, text);
        return JsonMap.Parse(text);
    }

    public Task<List<JsonMap>> GetMyTasksAsync(CancellationToken ct = default) =>
        GetListAsync("/api/warehouse/tasks/my", "tasks", ct);

    public async Task<JsonMap> GetTaskAsync(int taskId, CancellationToken ct = default)
    {
        var body = await WebJsonAsync("GET", $"/api/warehouse/tasks/{taskId}", null, 30, ct);
        return body.Obj("task") ?? body;
    }

    public Task<List<JsonMap>> GetTaskStatusesAsync(CancellationToken ct = default) =>
        GetListAsync("/api/warehouse/tasks/statuses", "task_statuses", ct);

    public async Task<JsonMap> PatchTaskAsync(int taskId, object body, CancellationToken ct = default)
    {
        var json = await WebJsonAsync("PATCH", $"/api/warehouse/tasks/{taskId}", body, 30, ct);
        return json.Obj("task") ?? json;
    }

    public Task<byte[]> DownloadTaskAttachmentAsync(int taskId, int attachmentId, CancellationToken ct = default) =>
        WebBytesAsync($"/api/warehouse/tasks/{taskId}/attachments/{attachmentId}", 120, ct);

    public Task<List<JsonMap>> SearchCatalogProductsAsync(string? q = null, CancellationToken ct = default)
    {
        var path = "/api/warehouse/catalog/products";
        if (!string.IsNullOrWhiteSpace(q))
            path += "?q=" + Uri.EscapeDataString(q.Trim());
        return GetListAsync(path, "products", ct);
    }

    public async Task<JsonMap> GetCatalogProductAsync(int productId, CancellationToken ct = default)
    {
        var body = await WebJsonAsync("GET", $"/api/warehouse/catalog/products/{productId}", null, 20, ct);
        return body.Obj("product") ?? body;
    }

    public Task<JsonMap> AddCatalogBarcodeAsync(int productId, string barcode, string label, string group, CancellationToken ct = default) =>
        WebJsonAsync("POST", $"/api/warehouse/catalog/products/{productId}/barcodes", new
        {
            barcode = barcode.Trim(),
            label = label.Trim(),
            group = group.Trim(),
        }, 20, ct);

    public Task<JsonMap> AddCatalogGtinAsync(int productId, string code, CancellationToken ct = default) =>
        WebJsonAsync("POST", $"/api/warehouse/catalog/products/{productId}/gtins", new
        {
            code = code.Trim(),
        }, 20, ct);

    public Task<JsonMap> AddCatalogBoxAsync(int productId, string barcode, int quantity, CancellationToken ct = default) =>
        WebJsonAsync("POST", $"/api/warehouse/catalog/products/{productId}/boxes", new
        {
            barcode = barcode.Trim(),
            quantity,
        }, 20, ct);

    public async Task<List<JsonMap>> FbsMyJobsAsync(CancellationToken ct = default)
    {
        var body = await ApiJsonAsync("GET", "/api/v1/fbs-packing/my", null, 30, ct);
        return body.Arr("jobs");
    }

    public async Task<JsonMap> FbsOpenJobAsync(int jobId, CancellationToken ct = default)
    {
        var body = await ApiJsonAsync("GET", $"/api/v1/fbs-packing/jobs/{jobId}/pack", null, 30, ct);
        return body.Obj("job") ?? body;
    }

    public Task<JsonMap> FbsScanProductAsync(int jobId, string barcode, bool batch, bool includePdf, bool autoClose, CancellationToken ct = default) =>
        ApiJsonAsync("POST", $"/api/v1/fbs-packing/jobs/{jobId}/scan-product", new
        {
            barcode,
            batch,
            include_pdf = includePdf,
            auto_close = autoClose,
        }, 60, ct);

    public Task<JsonMap> FbsPickSkuAsync(int jobId, string sku, int? productId, bool batch, bool includePdf, bool autoClose, CancellationToken ct = default)
    {
        object body = productId is int pid
            ? new { sku, product_id = pid, batch, include_pdf = includePdf, auto_close = autoClose }
            : new { sku, batch, include_pdf = includePdf, auto_close = autoClose };
        return ApiJsonAsync("POST", $"/api/v1/fbs-packing/jobs/{jobId}/pick-sku", body, 60, ct);
    }

    public Task<JsonMap> FbsScanLabelAsync(int jobId, string barcode, CancellationToken ct = default) =>
        ApiJsonAsync("POST", $"/api/v1/fbs-packing/jobs/{jobId}/scan-label", new { barcode }, 30, ct);

    public Task<JsonMap> FbsCloseLineAsync(int jobId, int lineId, CancellationToken ct = default) =>
        ApiJsonAsync("POST", $"/api/v1/fbs-packing/jobs/{jobId}/lines/{lineId}/close", null, 30, ct);

    public Task<JsonMap> FbsCancelPrintAsync(int jobId, int lineId, CancellationToken ct = default) =>
        ApiJsonAsync("POST", $"/api/v1/fbs-packing/jobs/{jobId}/lines/{lineId}/cancel-print", null, 30, ct);

    public Task<JsonMap> FbsSetLineStatusAsync(int jobId, int lineId, string status, CancellationToken ct = default) =>
        ApiJsonAsync("POST", $"/api/v1/fbs-packing/jobs/{jobId}/lines/{lineId}/set-status", new { status }, 30, ct);

    public Task<byte[]> FbsDownloadLinePdfAsync(int jobId, int lineId, CancellationToken ct = default) =>
        ApiBytesAsync($"/api/v1/fbs-packing/jobs/{jobId}/lines/{lineId}/label", 60, ct);

    public Task<byte[]> FbsDownloadLineLabelsZipAsync(int jobId, CancellationToken ct = default) =>
        ApiBytesAsync($"/api/v1/fbs-packing/jobs/{jobId}/line-labels.zip", 180, ct);

    public async Task<List<JsonMap>> OtherMpMyJobsAsync(CancellationToken ct = default)
    {
        var body = await ApiJsonAsync("GET", "/api/v1/other-marketplaces/my", null, 30, ct);
        return body.Arr("jobs");
    }

    public async Task<JsonMap> OtherMpOpenJobAsync(int jobId, CancellationToken ct = default)
    {
        var body = await ApiJsonAsync("GET", $"/api/v1/other-marketplaces/jobs/{jobId}/pack", null, 30, ct);
        return body.Obj("job") ?? body;
    }

    public Task<JsonMap> OtherMpScanAsync(int jobId, string barcode, CancellationToken ct = default) =>
        ApiJsonAsync("POST", $"/api/v1/other-marketplaces/jobs/{jobId}/scan", new { barcode }, 60, ct);

    public Task<JsonMap> OtherMpPickAsync(int jobId, string sku, int? productId, CancellationToken ct = default)
    {
        object body = productId is int pid ? new { sku, product_id = pid } : new { sku };
        return ApiJsonAsync("POST", $"/api/v1/other-marketplaces/jobs/{jobId}/pick", body, 60, ct);
    }

    public Task<JsonMap> OtherMpSetLineStatusAsync(int jobId, int lineId, string status, CancellationToken ct = default) =>
        ApiJsonAsync("POST", $"/api/v1/other-marketplaces/jobs/{jobId}/lines/{lineId}/set-status", new { status }, 30, ct);

    public Task<byte[]> OtherMpRouteSheetAsync(int jobId, string cargoType, int cargoCount, CancellationToken ct = default) =>
        ApiPostBytesAsync(
            $"/api/v1/other-marketplaces/jobs/{jobId}/route-sheet.pdf",
            new { cargo_type = cargoType, cargo_count = cargoCount },
            60,
            ct);

    public async Task<List<JsonMap>> FboMyJobsAsync(CancellationToken ct = default)
    {
        var body = await ApiJsonAsync("GET", "/api/v1/fbo-packing/my", null, 30, ct);
        return body.Arr("jobs");
    }

    public async Task<JsonMap> FboOpenJobAsync(int jobId, CancellationToken ct = default)
    {
        var body = await ApiJsonAsync("GET", $"/api/v1/fbo-packing/jobs/{jobId}/pack", null, 30, ct);
        return body.Obj("job") ?? body;
    }

    public Task<JsonMap> FboScanProductAsync(int jobId, string barcode, bool batch, bool includePdf, bool autoClose, CancellationToken ct = default) =>
        ApiJsonAsync("POST", $"/api/v1/fbo-packing/jobs/{jobId}/scan-product", new
        {
            barcode,
            batch,
            include_pdf = includePdf,
            auto_close = autoClose,
        }, 60, ct);

    public Task<JsonMap> FboPickSkuAsync(int jobId, string sku, int? productId, bool batch, bool includePdf, bool autoClose, CancellationToken ct = default)
    {
        object body = productId is int pid
            ? new { sku, product_id = pid, batch, include_pdf = includePdf, auto_close = autoClose }
            : new { sku, batch, include_pdf = includePdf, auto_close = autoClose };
        return ApiJsonAsync("POST", $"/api/v1/fbo-packing/jobs/{jobId}/pick-sku", body, 60, ct);
    }

    public Task<JsonMap> FboCloseLineAsync(int jobId, int lineId, CancellationToken ct = default) =>
        ApiJsonAsync("POST", $"/api/v1/fbo-packing/jobs/{jobId}/lines/{lineId}/close", null, 30, ct);

    public Task<JsonMap> FboCancelPrintAsync(int jobId, int lineId, CancellationToken ct = default) =>
        ApiJsonAsync("POST", $"/api/v1/fbo-packing/jobs/{jobId}/lines/{lineId}/cancel-print", null, 30, ct);

    public Task<JsonMap> FboSetLineStatusAsync(int jobId, int lineId, string status, CancellationToken ct = default) =>
        ApiJsonAsync("POST", $"/api/v1/fbo-packing/jobs/{jobId}/lines/{lineId}/set-status", new { status }, 30, ct);

    public Task<byte[]> FboDownloadLinePdfAsync(int jobId, int lineId, CancellationToken ct = default) =>
        ApiBytesAsync($"/api/v1/fbo-packing/jobs/{jobId}/lines/{lineId}/label", 60, ct);

    public Task<byte[]> FboDownloadLineLabelsZipAsync(int jobId, CancellationToken ct = default) =>
        ApiBytesAsync($"/api/v1/fbo-packing/jobs/{jobId}/line-labels.zip", 180, ct);

    public Task<byte[]> FboDownloadSupplyQrAsync(int jobId, CancellationToken ct = default) =>
        ApiBytesAsync($"/api/v1/fbo-packing/jobs/{jobId}/supply-qr.pdf", 60, ct);

    public Task<byte[]> FboDownloadPalletSheetsAsync(int jobId, CancellationToken ct = default) =>
        ApiBytesAsync($"/api/v1/fbo-packing/jobs/{jobId}/pallet-sheets.pdf", 120, ct);

    public async Task<List<JsonMap>> FboSheetMyJobsAsync(CancellationToken ct = default)
    {
        var body = await ApiJsonAsync("GET", "/api/v1/fbo-sheet-packing/my", null, 30, ct);
        return body.Arr("jobs");
    }

    public async Task<JsonMap> FboSheetOpenJobAsync(int jobId, CancellationToken ct = default)
    {
        var body = await ApiJsonAsync("GET", $"/api/v1/fbo-sheet-packing/jobs/{jobId}/pack", null, 30, ct);
        return body.Obj("job") ?? body;
    }

    public Task<JsonMap> FboSheetPrintBoxesAsync(int jobId, int count, CancellationToken ct = default) =>
        ApiJsonAsync("POST", $"/api/v1/fbo-sheet-packing/jobs/{jobId}/print-boxes", new { count }, 60, ct);

    public Task<JsonMap> FboSheetReprintBoxAsync(int jobId, int boxId, CancellationToken ct = default) =>
        ApiJsonAsync("POST", $"/api/v1/fbo-sheet-packing/jobs/{jobId}/reprint-box", new { box_id = boxId }, 60, ct);

    public Task<JsonMap> FboSheetPrintPalletsAsync(int jobId, int count, CancellationToken ct = default) =>
        ApiJsonAsync("POST", $"/api/v1/fbo-sheet-packing/jobs/{jobId}/print-pallets", new { count }, 60, ct);

    public Task<JsonMap> FboSheetReprintPalletAsync(int jobId, int palletId, CancellationToken ct = default) =>
        ApiJsonAsync("POST", $"/api/v1/fbo-sheet-packing/jobs/{jobId}/reprint-pallet", new { pallet_id = palletId }, 60, ct);

    public Task<JsonMap> FboSheetClosePalletAsync(int jobId, string barcode, CancellationToken ct = default) =>
        ApiJsonAsync("POST", $"/api/v1/fbo-sheet-packing/jobs/{jobId}/close-pallet", new { barcode }, 30, ct);

    public Task<JsonMap> FboSheetResolveAsync(int jobId, string barcode, CancellationToken ct = default) =>
        ApiJsonAsync("POST", $"/api/v1/fbo-sheet-packing/jobs/{jobId}/resolve", new { barcode }, 30, ct);

    public Task<JsonMap> FboSheetAssignAsync(
        int jobId,
        string barcode,
        string productBarcode,
        int quantity,
        string? productionDate = null,
        CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["barcode"] = barcode,
            ["product_barcode"] = productBarcode,
            ["quantity"] = quantity,
        };
        if (!string.IsNullOrWhiteSpace(productionDate))
            body["production_date"] = productionDate.Trim();
        return ApiJsonAsync("POST", $"/api/v1/fbo-sheet-packing/jobs/{jobId}/assign", body, 30, ct);
    }

    public Task<JsonMap> FboSheetUnassignAsync(
        int jobId,
        int boxId,
        string productBarcode = "",
        CancellationToken ct = default) =>
        ApiJsonAsync("POST", $"/api/v1/fbo-sheet-packing/jobs/{jobId}/unassign", new
        {
            box_id = boxId,
            product_barcode = productBarcode ?? "",
        }, 30, ct);

    public Task<JsonMap> FboSheetUnbindPalletAsync(
        int jobId,
        int boxId,
        CancellationToken ct = default) =>
        ApiJsonAsync("POST", $"/api/v1/fbo-sheet-packing/jobs/{jobId}/unbind-pallet", new
        {
            box_id = boxId,
        }, 30, ct);

    public void Dispose()
    {
        _web.Dispose();
        _apiHttp.Dispose();
    }

    private async Task TryLoginApiAsync(string login, string password, CancellationToken ct)
    {
        ApiOk = false;
        ApiError = "";
        if (ApiUrl.Length == 0)
        {
            ApiError = "Не задан адрес API (WAREHOUSE_API_URL / порт 8766)";
            return;
        }
        try
        {
            var json = JsonSerializer.Serialize(new { login = login.Trim(), password });
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await _apiHttp.PostAsync(Api("/api/v1/login"), content, ct);
            if ((int)resp.StatusCode is 400 or 401)
            {
                ApiError = await DetailAsync(resp) ?? "Ошибка входа в API";
                return;
            }
            if (!resp.IsSuccessStatusCode)
            {
                ApiError = await DetailAsync(resp) ?? $"HTTP {(int)resp.StatusCode}";
                return;
            }
            ApiOk = true;
        }
        catch (Exception ex)
        {
            ApiError = $"Не удалось подключиться к API ({ApiUrl}): {ex.Message}. Запустите python run_api.py (порт 8766).";
        }
    }

    private async Task<List<JsonMap>> GetListAsync(string path, string key, CancellationToken ct)
    {
        var body = await WebJsonAsync("GET", path, null, 60, ct);
        return body.Arr(key);
    }

    private async Task<JsonMap> WebJsonAsync(string method, string path, object? body, int timeoutSec, CancellationToken ct)
    {
        using var resp = await SendAsync(_web, method, Web(path), body, timeoutSec, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        Raise(resp, text);
        return JsonMap.Parse(text);
    }

    private async Task<JsonMap> ApiJsonAsync(string method, string path, object? body, int timeoutSec, CancellationToken ct)
    {
        if (ApiUrl.Length == 0)
            throw new ApiException("Не задан адрес API (run_api.py)");
        if (!ApiOk)
            throw new ApiException(ApiError.Length > 0 ? ApiError : "Нет сессии API — войдите снова");
        using var resp = await SendAsync(_apiHttp, method, Api(path), body, timeoutSec, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        Raise(resp, text);
        return JsonMap.Parse(text);
    }

    private async Task<byte[]> WebBytesAsync(string path, int timeoutSec, CancellationToken ct)
    {
        using var resp = await SendAsync(_web, "GET", Web(path), null, timeoutSec, ct);
        var data = await resp.Content.ReadAsByteArrayAsync(ct);
        if (!resp.IsSuccessStatusCode)
            Raise(resp, Encoding.UTF8.GetString(data));
        return data;
    }

    private async Task<byte[]> ApiPostBytesAsync(string path, object body, int timeoutSec, CancellationToken ct)
    {
        if (!ApiOk)
            throw new ApiException(ApiError.Length > 0 ? ApiError : "Нет сессии API — войдите снова");
        using var resp = await SendAsync(_apiHttp, "POST", Api(path), body, timeoutSec, ct);
        var data = await resp.Content.ReadAsByteArrayAsync(ct);
        if (!resp.IsSuccessStatusCode)
            Raise(resp, Encoding.UTF8.GetString(data));
        return data;
    }

    private async Task<byte[]> ApiBytesAsync(string path, int timeoutSec, CancellationToken ct)
    {
        if (!ApiOk)
            throw new ApiException(ApiError.Length > 0 ? ApiError : "Нет сессии API — войдите снова");
        using var resp = await SendAsync(_apiHttp, "GET", Api(path), null, timeoutSec, ct);
        var data = await resp.Content.ReadAsByteArrayAsync(ct);
        if (!resp.IsSuccessStatusCode)
            Raise(resp, Encoding.UTF8.GetString(data));
        return data;
    }

    /// The timeout guards the wait for response headers only, matching the
    /// python client's read timeout. Downloading the body of a large job must
    /// not race a deadline: a job with hundreds of lines legitimately takes
    /// longer than any sane header timeout.
    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string method, string url, object? body, int timeoutSec, CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
            var req = new HttpRequestMessage(new HttpMethod(method), url);
            if (body != null)
                req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            try
            {
                return await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                req.Dispose();
                throw new ApiException($"Сервер не ответил за {timeoutSec} с: {url}", 408);
            }
            catch (Exception ex) when (IsTransientNetwork(ex))
            {
                req.Dispose();
                last = ex;
                if (attempt >= 3)
                    break;
                await Task.Delay(TimeSpan.FromMilliseconds(150 * attempt), ct);
            }
            catch
            {
                req.Dispose();
                throw;
            }
        }
        throw new ApiException("Нет связи с сервером. Повторите пик.", last);
    }

    private static bool IsTransientNetwork(Exception ex)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            if (current is OperationCanceledException)
                return false;
        }
        for (var current = ex; current != null; current = current.InnerException)
        {
            if (current is HttpRequestException or SocketException or IOException)
                return true;
            if (current.GetType().Name is "HttpIOException" or "WinHttpException")
                return true;
        }
        return false;
    }

    private static void Raise(HttpResponseMessage resp, string text)
    {
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            throw new AuthException("Сессия истекла — войдите снова");
        if ((int)resp.StatusCode >= 400)
            throw new ApiException(ParseDetail(text, (int)resp.StatusCode), (int)resp.StatusCode);
    }

    private static async Task<string?> DetailAsync(HttpResponseMessage resp)
    {
        var text = await resp.Content.ReadAsStringAsync();
        return ParseDetail(text, (int)resp.StatusCode);
    }

    private static string ParseDetail(string text, int status)
    {
        if (string.IsNullOrWhiteSpace(text))
            return $"HTTP {status}";
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("detail", out var detail))
            {
                if (detail.ValueKind == JsonValueKind.String)
                    return detail.GetString() ?? $"HTTP {status}";
                if (detail.ValueKind == JsonValueKind.Array)
                {
                    var parts = detail.EnumerateArray()
                        .Select(x => x.ValueKind == JsonValueKind.Object && x.TryGetProperty("msg", out var msg) ? msg.ToString() : x.ToString())
                        .Where(s => s.Length > 0);
                    var joined = string.Join("; ", parts);
                    if (joined.Length > 0)
                        return joined;
                }
            }
        }
        catch { }
        return text.Length > 400 ? text[..400] : text;
    }

    private string Web(string path) => ServerUrl + path;
    private string Api(string path) => ApiUrl + path;

    private static HttpClient NewClient(CookieContainer cookies)
    {
        var handler = new SocketsHttpHandler
        {
            CookieContainer = cookies,
            UseCookies = true,
            AllowAutoRedirect = true,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(4),
            MaxConnectionsPerServer = 8,
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.ExpectContinue = false;
        return client;
    }
}
