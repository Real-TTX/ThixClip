using MatCloud.Contract;
using MatCloud.Contract.Clip;
using ThixClip.Core.Infrastructure;
using ThixClip.Core.Models;

namespace ThixClip.Core.Business;

public enum CloudState
{
    Off,
    Connecting,
    Online,
    Syncing,
    Offline,
    SignedOut,
}

/// <summary>What the cloud connection does right now (settings page, sidebar, tray).</summary>
public sealed record SyncStatus(CloudState State, string Message, DateTimeOffset? LastSync = null)
{
    public bool IsConnected => State is CloudState.Online or CloudState.Syncing or CloudState.Offline or CloudState.Connecting;
}

/// <summary>
/// The connection to MatCloud (the clip module, see <see cref="ClipContract"/>): signing the device in (in the browser or with a password) and
/// out, and one background loop that keeps the history the same on every device:
///   push local changes -> upload new images -> pull the changes of the account -> download missing images -> long poll.
/// A local change wakes the loop at once (<see cref="Kick"/>), a change elsewhere ends the long poll. Without network the
/// loop waits and tries again (2 s, 4 s ... 60 s); a removed device (401) signs out.
/// </summary>
public static class SyncService
{
    private const int LongPollSeconds = 25;
    private const int ImageDownloadsPerRound = 20;
    private const string NotConnected = "Nicht verbunden";

    private static readonly object Gate = new();
    private static CancellationTokenSource? _stop;
    private static CancellationTokenSource _kick = new();
    private static Task? _loop;
    private static SyncStatus _status = new(CloudState.Off, NotConnected);

    /// <summary>Raised when <see cref="Status"/> changes (any thread).</summary>
    public static event EventHandler<SyncStatus>? StatusChanged;

    public static SyncStatus Status => _status;

    public static bool IsConnected => SettingsService.Get().Cloud.IsConnected;

    /// <summary>
    /// Signs this computer in at MatCloud in the browser (works with every account, also one of MatIdentity without a password): MatCloud
    /// answers a code, <paramref name="onCode"/> shows it, <paramref name="openAddress"/> opens the page where the person confirms it; then
    /// this waits until it is confirmed, refused or expired. Returns false when <paramref name="cancellationToken"/> ended the wait. The
    /// token that comes back is kept protected, everything already in the history goes up.
    /// </summary>
    public static async Task<bool> ConnectInBrowserAsync(string serverUrl, string deviceName, Action<DeviceLinkStart> onCode, Action<string> openAddress,
        CancellationToken cancellationToken = default)
    {
        AccessPolicy.EnsureCloudAllowed();
        var url = NormalizeServerUrl(serverUrl);
        deviceName = CheckDeviceName(deviceName);
        if (IsConnected)
        {
            await DisconnectAsync();
        }

        SetStatus(new SyncStatus(CloudState.Connecting, "Warte auf die Bestätigung im Browser …"));
        DeviceLoginResponse login;
        try
        {
            using var client = CloudClient.Create(url, null);
            var start = await client.StartLinkAsync(deviceName, cancellationToken);
            onCode(start);
            openAddress(start.VerificationUrl);
            login = await client.WaitForLinkAsync(start, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetStatus(new SyncStatus(CloudState.Off, NotConnected));
            return false;
        }
        catch (Exception exception) when (exception is CloudException or HttpRequestException or TaskCanceledException or UriFormatException or System.Text.Json.JsonException)
        {
            SetStatus(new SyncStatus(CloudState.Off, NotConnected));
            throw SignInFailed(exception, url);
        }

        await CompleteSignInAsync(url, login);
        return true;
    }

    /// <summary>
    /// Signs this computer in at MatCloud with the user name and password of a local account. The password is used once and never stored;
    /// the device token that comes back is kept protected. Everything already in the history goes up.
    /// </summary>
    public static async Task ConnectAsync(string serverUrl, string userName, string password, string deviceName, CancellationToken cancellationToken = default)
    {
        AccessPolicy.EnsureCloudAllowed();
        var url = NormalizeServerUrl(serverUrl);
        if (string.IsNullOrWhiteSpace(userName))
        {
            throw new BusinessException("Bitte den Benutzernamen angeben.", nameof(CloudSettings.UserName));
        }

        if (string.IsNullOrEmpty(password))
        {
            throw new BusinessException("Bitte das Passwort angeben.", "Password");
        }

        deviceName = CheckDeviceName(deviceName);
        if (IsConnected)
        {
            await DisconnectAsync();
        }

        SetStatus(new SyncStatus(CloudState.Connecting, "Verbinde …"));
        DeviceLoginResponse login;
        try
        {
            using var client = CloudClient.Create(url, null);
            login = await client.LoginAsync(userName.Trim(), password, deviceName, cancellationToken);
        }
        catch (CloudException exception) when (exception.IsUnauthorized)
        {
            SetStatus(new SyncStatus(CloudState.Off, NotConnected));
            throw new BusinessException("Benutzername oder Passwort stimmen nicht.", "Password");
        }
        catch (Exception exception) when (exception is CloudException or HttpRequestException or TaskCanceledException or UriFormatException or System.Text.Json.JsonException)
        {
            SetStatus(new SyncStatus(CloudState.Off, NotConnected));
            throw SignInFailed(exception, url);
        }

        await CompleteSignInAsync(url, login);
    }

    /// <summary>Signs this computer out (the cloud forgets the device); the local history stays.</summary>
    public static async Task DisconnectAsync()
    {
        var cloud = SettingsService.Get().Cloud;
        Stop();
        if (cloud.IsConnected && ReadToken(cloud) is { } token)
        {
            try
            {
                using var client = CloudClient.Create(cloud.ServerUrl, token);
                await client.LogoutAsync(CancellationToken.None);
            }
            catch (Exception exception) when (exception is CloudException or HttpRequestException or TaskCanceledException)
            {
                Log.Warning($"Logout at the cloud failed ({exception.Message}); signing out locally");
            }
        }

        Forget(cloud);
        SetStatus(new SyncStatus(CloudState.Off, NotConnected));
    }

    /// <summary>Starts the background loop when this computer is connected.</summary>
    public static void Start()
    {
        var cloud = SettingsService.Get().Cloud;
        if (!cloud.IsConnected || AccessPolicy.IsCloudDisabled)
        {
            SetStatus(new SyncStatus(CloudState.Off, AccessPolicy.IsCloudDisabled ? "Durch Richtlinie deaktiviert" : NotConnected));
            return;
        }

        lock (Gate)
        {
            if (_loop is { IsCompleted: false })
            {
                return;
            }

            _stop = new CancellationTokenSource();
            var token = _stop.Token;
            _loop = Task.Run(() => RunAsync(token), token);
        }
    }

    public static void Stop()
    {
        lock (Gate)
        {
            _stop?.Cancel();
            _stop = null;
            _kick.Cancel();
        }

        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
        }

        _loop = null;
    }

    /// <summary>Something changed here (or the network is back): sync now instead of waiting.</summary>
    public static void Kick()
    {
        lock (Gate)
        {
            _kick.Cancel();
        }
    }

    /// <summary>Tells MatCloud the new retention of the account (PUT <see cref="ClipContract.Settings"/>).</summary>
    internal static async Task PushRetentionAsync(int days)
    {
        var cloud = SettingsService.Get().Cloud;
        if (!cloud.IsConnected || ReadToken(cloud) is not { } token)
        {
            return;
        }

        try
        {
            using var client = CloudClient.Create(cloud.ServerUrl, token);
            await client.PutSettingsAsync(new ClipSettings(days), CancellationToken.None);
        }
        catch (Exception exception) when (exception is CloudException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            throw new BusinessException($"Die Speicherdauer konnte nicht in der Cloud gespeichert werden: {exception.Message}", nameof(AppSettings.RetentionDays));
        }
    }

    /// <summary>The address as it is stored: http(s) ("https://" when it is missing), no trailing slash, no query.</summary>
    public static string NormalizeServerUrl(string serverUrl)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            throw new BusinessException("Bitte die Adresse der Cloud angeben.", nameof(CloudSettings.ServerUrl));
        }

        try
        {
            return MatCloudClient.NormalizeServerUrl(serverUrl);
        }
        catch (UriFormatException)
        {
            throw new BusinessException("Das ist keine gültige Adresse (z. B. https://cloud.example.com).", nameof(CloudSettings.ServerUrl));
        }
    }

    private static string CheckDeviceName(string deviceName)
    {
        deviceName = string.IsNullOrWhiteSpace(deviceName) ? AppInfo.MachineName : deviceName.Trim();
        if (deviceName.Length > CloudContract.MaxDeviceNameLength)
        {
            throw new BusinessException($"Der Gerätename ist zu lang (höchstens {CloudContract.MaxDeviceNameLength} Zeichen).", nameof(CloudSettings.DeviceName));
        }

        return deviceName;
    }

    /// <summary>A failed sign-in for the person: the message of MatCloud at its field, or "not reachable" at the address.</summary>
    private static BusinessException SignInFailed(Exception exception, string url) => exception is CloudException cloudException
        ? new BusinessException(cloudException.Message, FieldOf(cloudException.Field))
        : new BusinessException($"Die MatCloud ist unter {url} nicht erreichbar.", nameof(CloudSettings.ServerUrl));

    /// <summary>A field MatCloud names ("deviceName") as the form here names it ("DeviceName"); the app itself has no field.</summary>
    private static string? FieldOf(string? field) =>
        string.IsNullOrEmpty(field) || field == "app" ? null : char.ToUpperInvariant(field[0]) + field[1..];

    /// <summary>
    /// Keeps the sign-in (the token protected, the account in the settings), takes the retention of the account (GET
    /// <see cref="ClipContract.Settings"/>), marks the history for upload and starts the loop.
    /// </summary>
    private static async Task CompleteSignInAsync(string url, DeviceLoginResponse login)
    {
        var protectedToken = TokenStore.Save(AccountKey(url, login.Account.UserName), login.Token);
        SettingsService.UpdateCloud(_ => new CloudSettings
        {
            ServerUrl = url,
            UserName = login.Account.UserName,
            DisplayName = login.Account.DisplayName,
            DeviceName = login.Account.DeviceName,
            DeviceId = login.DeviceId,
            LastRevision = 0,
            ProtectedToken = protectedToken,
        });

        try
        {
            using var client = CloudClient.Create(url, login.Token);
            var settings = await client.GetSettingsAsync(CancellationToken.None);
            SettingsService.ApplyRetentionFromCloud(settings.RetentionDays);
        }
        catch (Exception exception) when (exception is CloudException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            // The first change set brings the retention as well.
            Log.Warning($"Reading the clip settings failed ({exception.Message})");
        }

        ClipService.MarkAllForUpload();
        Log.Info($"Connected to {url} as device {login.DeviceId}");
        Start();
    }

    private static async Task RunAsync(CancellationToken stopToken)
    {
        var delay = TimeSpan.FromSeconds(2);
        var imagesToFetch = new Queue<ClipEntry>(ClipService.MissingImages());
        while (!stopToken.IsCancellationRequested)
        {
            CancellationToken kickToken;
            lock (Gate)
            {
                if (_kick.IsCancellationRequested)
                {
                    _kick.Dispose();
                    _kick = new CancellationTokenSource();
                }

                kickToken = _kick.Token;
            }

            var cloud = SettingsService.Get().Cloud;
            var token = ReadToken(cloud);
            if (!cloud.IsConnected || token is null)
            {
                SignedOut(cloud, "Der Zugang zur Cloud fehlt – bitte neu anmelden.");
                return;
            }

            try
            {
                using var client = CloudClient.Create(cloud.ServerUrl, token);
                SetStatus(_status with { State = CloudState.Syncing, Message = "Synchronisiere …" });
                await RoundAsync(client, imagesToFetch, stopToken);
                SetStatus(new SyncStatus(CloudState.Online, "Synchronisiert", DateTimeOffset.Now));
                delay = TimeSpan.FromSeconds(2);

                if (imagesToFetch.Count > 0 || ClipService.PendingChanges(1).Count > 0)
                {
                    continue;
                }

                using var waitToken = CancellationTokenSource.CreateLinkedTokenSource(stopToken, kickToken);
                await client.WaitAsync(SettingsService.Get().Cloud.LastRevision, LongPollSeconds, waitToken.Token);
            }
            catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException) when (kickToken.IsCancellationRequested)
            {
                // a local change: next round right away
            }
            catch (CloudException exception) when (exception.IsUnauthorized)
            {
                SignedOut(cloud, "Dieses Gerät wurde in der Cloud entfernt – bitte neu anmelden.");
                return;
            }
            catch (Exception exception) when (exception is CloudException or HttpRequestException or TaskCanceledException or IOException or System.Text.Json.JsonException)
            {
                Log.Warning($"Sync: {exception.GetType().Name}: {exception.Message}");
                SetStatus(new SyncStatus(CloudState.Offline, exception is CloudException ? exception.Message : "Cloud nicht erreichbar – neuer Versuch folgt", _status.LastSync));
                try
                {
                    using var waitToken = CancellationTokenSource.CreateLinkedTokenSource(stopToken, kickToken);
                    await Task.Delay(delay, waitToken.Token);
                }
                catch (OperationCanceledException) when (!stopToken.IsCancellationRequested)
                {
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                delay = TimeSpan.FromSeconds(Math.Min(60, delay.TotalSeconds * 2));
            }
        }
    }

    /// <summary>Tests start disconnected.</summary>
    internal static void ResetForTests()
    {
        Stop();
        _status = new SyncStatus(CloudState.Off, NotConnected);
    }

    /// <summary>One round without the long poll (tests).</summary>
    internal static async Task SyncOnceAsync(CancellationToken cancellationToken = default)
    {
        var cloud = SettingsService.Get().Cloud;
        var token = ReadToken(cloud) ?? throw new InvalidOperationException("This computer is not connected.");
        using var client = CloudClient.Create(cloud.ServerUrl, token);
        await RoundAsync(client, new Queue<ClipEntry>(ClipService.MissingImages()), cancellationToken);
    }

    /// <summary>push local changes -> upload new images -> pull the account's changes -> download missing images.</summary>
    private static async Task RoundAsync(CloudClient client, Queue<ClipEntry> imagesToFetch, CancellationToken cancellationToken)
    {
        await PushAsync(client, cancellationToken);
        await UploadImagesAsync(client, cancellationToken);
        foreach (var entry in await PullAsync(client, cancellationToken))
        {
            imagesToFetch.Enqueue(entry);
        }

        await DownloadImagesAsync(client, imagesToFetch, cancellationToken);
    }

    private static async Task PushAsync(CloudClient client, CancellationToken cancellationToken)
    {
        for (var round = 0; round < 50; round++)
        {
            var pending = ClipService.PendingChanges(ClipContract.MaxBatchSize);
            if (pending.Count == 0)
            {
                return;
            }

            var sent = pending.ToDictionary(item => item.Entry.SyncId, item => item.Entry.UpdateDate);
            var clips = pending.Select(item => ClipService.ToDto(item.Entry, item.IsDeleted)).ToList();
            try
            {
                var response = await client.PushAsync(new ClipPushRequest(clips), cancellationToken);
                foreach (var result in response.Results)
                {
                    ClipService.ConfirmPushed(result, sent.GetValueOrDefault(result.SyncId));
                }
            }
            catch (CloudException exception) when (exception.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                // The cloud refuses a whole push for one clip it does not accept: push one by one, so that one clip
                // never holds up the others; the refused one stays on this computer only.
                await PushOneByOneAsync(client, clips, sent, cancellationToken);
            }
        }
    }

    private static async Task PushOneByOneAsync(CloudClient client, List<ClipDto> clips, Dictionary<Guid, DateTimeOffset> sent, CancellationToken cancellationToken)
    {
        foreach (var clip in clips)
        {
            try
            {
                var response = await client.PushAsync(new ClipPushRequest([clip]), cancellationToken);
                foreach (var result in response.Results)
                {
                    ClipService.ConfirmPushed(result, sent.GetValueOrDefault(result.SyncId));
                }
            }
            catch (CloudException exception) when (exception.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                Log.Warning($"The cloud refused entry {clip.SyncId}: {exception.Message}");
                ClipService.KeepLocalOnly(clip.SyncId);
            }
        }
    }

    private static async Task UploadImagesAsync(CloudClient client, CancellationToken cancellationToken)
    {
        foreach (var entry in ClipService.PendingImageUploads())
        {
            var png = ClipService.ReadImage(entry.Id);
            if (png is null)
            {
                continue;
            }

            await client.UploadImageAsync(entry.SyncId, png, cancellationToken);
            ClipService.ConfirmImageUploaded(entry.Id);
        }
    }

    /// <summary>Applies everything above the last revision; returns the entries whose PNG has to be downloaded.</summary>
    private static async Task<List<ClipEntry>> PullAsync(CloudClient client, CancellationToken cancellationToken)
    {
        var missing = new List<ClipEntry>();
        while (true)
        {
            var since = SettingsService.Get().Cloud.LastRevision;
            var changes = await client.GetChangesAsync(since, ClipContract.MaxBatchSize, cancellationToken);
            foreach (var clip in changes.Clips)
            {
                if (ClipService.ApplyRemote(clip) is { } needsImage)
                {
                    missing.Add(needsImage);
                }
            }

            var reached = changes.HasMore && changes.Clips.Count > 0 ? changes.Clips.Max(clip => clip.Revision) : Math.Max(changes.Latest, since);
            SettingsService.UpdateCloud(cloud => cloud with { LastRevision = Math.Max(cloud.LastRevision, reached) });
            SettingsService.ApplyRetentionFromCloud(changes.RetentionDays);
            if (!changes.HasMore || changes.Clips.Count == 0)
            {
                return missing;
            }
        }
    }

    private static async Task DownloadImagesAsync(CloudClient client, Queue<ClipEntry> queue, CancellationToken cancellationToken)
    {
        for (var count = 0; count < ImageDownloadsPerRound && queue.Count > 0; count++)
        {
            var entry = queue.Dequeue();
            var current = ClipService.Get(entry.Id);
            if (current is not { IsImage: true, HasImage: false })
            {
                continue;
            }

            var png = await client.DownloadImageAsync(current.SyncId, cancellationToken);
            if (png is { Length: > 0 })
            {
                ClipService.SetImageFromCloud(current.Id, png);
            }
        }
    }

    private static void SignedOut(CloudSettings cloud, string message)
    {
        Log.Warning($"Sync: signed out ({message})");
        Forget(cloud);
        SetStatus(new SyncStatus(CloudState.SignedOut, message));
    }

    private static void Forget(CloudSettings cloud)
    {
        if (cloud.ServerUrl.Length > 0)
        {
            TokenStore.Delete(AccountKey(cloud.ServerUrl, cloud.UserName));
        }

        SettingsService.UpdateCloud(current => new CloudSettings { ServerUrl = current.ServerUrl, UserName = current.UserName, DeviceName = current.DeviceName });
        ClipService.ForgetTombstones();
    }

    private static string? ReadToken(CloudSettings cloud) =>
        cloud.IsConnected ? TokenStore.Read(AccountKey(cloud.ServerUrl, cloud.UserName), cloud.ProtectedToken) : null;

    private static string AccountKey(string serverUrl, string userName) => $"{userName}@{serverUrl}";

    private static void SetStatus(SyncStatus status)
    {
        if (status == _status)
        {
            return;
        }

        _status = status;
        StatusChanged?.Invoke(null, status);
    }
}
