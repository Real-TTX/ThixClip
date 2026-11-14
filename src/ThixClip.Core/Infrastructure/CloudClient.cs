using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ThixClip.Shared;

namespace ThixClip.Core.Infrastructure;

/// <summary>The cloud refused a request; <see cref="StatusCode"/> 401 means the device is no longer signed in.</summary>
internal sealed class CloudException(HttpStatusCode statusCode, string message, string? field = null) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    public string? Field { get; } = field;

    public bool IsUnauthorized => StatusCode == HttpStatusCode.Unauthorized;
}

/// <summary>
/// HTTP calls of the sync protocol (<see cref="SyncContract"/>). One instance per server and token. Only
/// <see cref="Business.SyncService"/> uses it.
/// </summary>
internal sealed class CloudClient : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TransferTimeout = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http;

    public CloudClient(string serverUrl, string? token, HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.BaseAddress = new Uri(serverUrl.TrimEnd('/') + "/");
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"ThixClip/{AppInfo.BuildNumber}");
        _http.DefaultRequestHeaders.Add(SyncContract.ProtocolHeader, SyncContract.ProtocolVersion.ToString());
        if (token is not null)
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    /// <summary>Tests talk to an in-memory server.</summary>
    internal static Func<HttpMessageHandler>? HandlerForTests { get; set; }

    public static CloudClient Create(string serverUrl, string? token) => new(serverUrl, token, HandlerForTests?.Invoke());

    public Task<DeviceLoginResponse> LoginAsync(DeviceLoginRequest request, CancellationToken cancellationToken) =>
        SendAsync<DeviceLoginResponse>(HttpMethod.Post, SyncContract.DeviceLogin, JsonContent.Create(request, options: SyncContract.Json), RequestTimeout, cancellationToken);

    public Task LogoutAsync(CancellationToken cancellationToken) =>
        SendAsync<object>(HttpMethod.Post, SyncContract.DeviceLogout, null, RequestTimeout, cancellationToken, expectBody: false);

    public Task<AccountInfo> GetAccountAsync(CancellationToken cancellationToken) =>
        SendAsync<AccountInfo>(HttpMethod.Get, SyncContract.Account, null, RequestTimeout, cancellationToken);

    public Task<AccountInfo> UpdateAccountAsync(AccountSettingsUpdate update, CancellationToken cancellationToken) =>
        SendAsync<AccountInfo>(HttpMethod.Put, SyncContract.Account, JsonContent.Create(update, options: SyncContract.Json), RequestTimeout, cancellationToken);

    public Task<ChangeSet> GetChangesAsync(long since, int limit, CancellationToken cancellationToken) =>
        SendAsync<ChangeSet>(HttpMethod.Get, $"{SyncContract.Changes}?since={since}&limit={limit}", null, RequestTimeout, cancellationToken);

    public Task<WaitResult> WaitAsync(long since, int timeoutSeconds, CancellationToken cancellationToken) =>
        SendAsync<WaitResult>(HttpMethod.Get, $"{SyncContract.Wait}?since={since}&timeout={timeoutSeconds}", null,
            TimeSpan.FromSeconds(timeoutSeconds + 20), cancellationToken);

    public Task<PushResponse> PushAsync(PushRequest request, CancellationToken cancellationToken) =>
        SendAsync<PushResponse>(HttpMethod.Post, SyncContract.Push, JsonContent.Create(request, options: SyncContract.Json), RequestTimeout, cancellationToken);

    public async Task UploadImageAsync(Guid syncId, byte[] png, CancellationToken cancellationToken)
    {
        var content = new ByteArrayContent(png);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        await SendAsync<object>(HttpMethod.Put, SyncContract.Image(syncId), content, TransferTimeout, cancellationToken, expectBody: false);
    }

    public async Task<byte[]?> DownloadImageAsync(Guid syncId, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TransferTimeout);
        using var response = await _http.GetAsync(SyncContract.Image(syncId).TrimStart('/'), timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await ThrowIfFailedAsync(response, timeout.Token);
        return await response.Content.ReadAsByteArrayAsync(timeout.Token);
    }

    public void Dispose() => _http.Dispose();

    private async Task<T> SendAsync<T>(HttpMethod method, string path, HttpContent? content, TimeSpan timeoutAfter,
        CancellationToken cancellationToken, bool expectBody = true)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutAfter);
        using var request = new HttpRequestMessage(method, path.TrimStart('/')) { Content = content };
        using var response = await _http.SendAsync(request, timeout.Token);
        await ThrowIfFailedAsync(response, timeout.Token);
        if (!expectBody)
        {
            return default!;
        }

        return await response.Content.ReadFromJsonAsync<T>(SyncContract.Json, timeout.Token)
               ?? throw new CloudException(response.StatusCode, "Die Cloud hat eine leere Antwort geschickt.");
    }

    private static async Task ThrowIfFailedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        ApiError? error = null;
        try
        {
            if (response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
            {
                error = await response.Content.ReadFromJsonAsync<ApiError>(SyncContract.Json, cancellationToken);
            }
        }
        catch (JsonException)
        {
        }

        var message = error?.Message ?? response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Anmeldung abgelehnt.",
            HttpStatusCode.Forbidden => "Keine Berechtigung.",
            HttpStatusCode.NotFound => "Diese Adresse ist keine ThixClip.Cloud.",
            HttpStatusCode.TooManyRequests => "Zu viele Versuche – bitte kurz warten.",
            _ => $"Die Cloud antwortet mit Fehler {(int)response.StatusCode}.",
        };
        throw new CloudException(response.StatusCode, message, error?.Field);
    }
}
