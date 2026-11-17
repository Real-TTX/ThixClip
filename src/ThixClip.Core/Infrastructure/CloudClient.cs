using System.Net.Http.Json;
using MatCloud.Contract;
using MatCloud.Contract.Clip;

namespace ThixClip.Core.Infrastructure;

/// <summary>
/// The HTTP calls of ThixClip to MatCloud: a thin wrapper around <see cref="MatCloudClient"/> (the copied contract) for the sign-in, the account
/// and the routes of the clip module (<see cref="ClipContract"/>). One instance per server and token. Only <see cref="Business.SyncService"/> uses
/// it. Errors are <see cref="CloudException"/> of the contract.
/// </summary>
internal sealed class CloudClient : IDisposable
{
    private readonly MatCloudClient _client;

    private CloudClient(string serverUrl, string? token) =>
        _client = new MatCloudClient(serverUrl, token, UserAgent, HandlerForTests?.Invoke());

    /// <summary>Tests talk to an in-memory server.</summary>
    internal static Func<HttpMessageHandler>? HandlerForTests { get; set; }

    /// <summary>"ThixClip/0.1.42-20261115 (Windows)": the cloud shows the version with the device.</summary>
    public static string UserAgent => $"ThixClip/{Token(AppInfo.Version)} ({Token(AppInfo.PlatformName)})";

    public static CloudClient Create(string serverUrl, string? token) => new(serverUrl, token);

    public Task<DeviceLoginResponse> LoginAsync(string userName, string password, string deviceName, CancellationToken cancellationToken) =>
        _client.LoginAsync(new DeviceLoginRequest(userName, password, deviceName, AppInfo.PlatformName, AppInfo.Version, CloudApps.Clip), cancellationToken);

    public Task<DeviceLinkStart> StartLinkAsync(string deviceName, CancellationToken cancellationToken) =>
        _client.StartLinkAsync(new DeviceLinkRequest(deviceName, AppInfo.PlatformName, AppInfo.Version, CloudApps.Clip), cancellationToken);

    public Task<DeviceLoginResponse> WaitForLinkAsync(DeviceLinkStart start, CancellationToken cancellationToken) =>
        _client.WaitForLinkAsync(start, cancellationToken);

    public Task LogoutAsync(CancellationToken cancellationToken) => _client.LogoutAsync(cancellationToken);

    public Task<AccountInfo> GetAccountAsync(CancellationToken cancellationToken) => _client.GetAccountAsync(cancellationToken);

    public Task<ClipSettings> GetSettingsAsync(CancellationToken cancellationToken) =>
        _client.SendAsync<ClipSettings>(HttpMethod.Get, ClipContract.Settings, null, MatCloudClient.RequestTimeout, cancellationToken);

    public Task<ClipSettings> PutSettingsAsync(ClipSettings settings, CancellationToken cancellationToken) =>
        _client.SendAsync<ClipSettings>(HttpMethod.Put, ClipContract.Settings, JsonContent.Create(settings, options: CloudContract.Json),
            MatCloudClient.RequestTimeout, cancellationToken);

    public Task<ClipChangeSet> GetChangesAsync(long since, int limit, CancellationToken cancellationToken) =>
        _client.SendAsync<ClipChangeSet>(HttpMethod.Get, $"{ClipContract.Changes}?since={since}&limit={limit}", null, MatCloudClient.RequestTimeout, cancellationToken);

    public Task<WaitResult> WaitAsync(long since, int timeoutSeconds, CancellationToken cancellationToken) =>
        _client.SendAsync<WaitResult>(HttpMethod.Get, $"{ClipContract.Wait}?since={since}&timeout={timeoutSeconds}", null,
            TimeSpan.FromSeconds(timeoutSeconds + 20), cancellationToken);

    public Task<ClipPushResponse> PushAsync(ClipPushRequest request, CancellationToken cancellationToken) =>
        _client.SendAsync<ClipPushResponse>(HttpMethod.Post, ClipContract.Push, JsonContent.Create(request, options: CloudContract.Json),
            MatCloudClient.TransferTimeout, cancellationToken);

    public Task UploadImageAsync(Guid syncId, byte[] png, CancellationToken cancellationToken) =>
        _client.PutBytesAsync(ClipContract.Image(syncId), png, "image/png", cancellationToken);

    public Task<byte[]?> DownloadImageAsync(Guid syncId, CancellationToken cancellationToken) =>
        _client.GetBytesAsync(ClipContract.Image(syncId), cancellationToken);

    public void Dispose() => _client.Dispose();

    /// <summary>Only what a User-Agent header allows (letters, digits, '.', '-', '_').</summary>
    private static string Token(string text)
    {
        var token = new string(text.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-').ToArray()).Trim('-');
        return token.Length > 0 ? token : "unknown";
    }
}
