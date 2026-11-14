using System.Net;
using System.Net.Http.Json;
using ThixClip.Shared;

namespace ThixClip.Tests;

/// <summary>
/// ThixClip.Cloud in memory, after the rules of <see cref="SyncContract"/>: one revision counter, tombstones,
/// last writer wins, deleting wins, the same content under another key is merged. Enough to test the desktop side.
/// </summary>
internal sealed class FakeCloud : HttpMessageHandler
{
    public const string UserName = "anna";
    public const string Password = "geheim-123";

    private readonly object _gate = new();

    public Dictionary<Guid, ClipDto> Clips { get; } = [];

    public Dictionary<Guid, byte[]> Images { get; } = [];

    public HashSet<string> Tokens { get; } = [];

    public long Revision { get; private set; }

    public int RetentionDays { get; set; } = SyncContract.DefaultRetentionDays;

    /// <summary>A change made on another device or in the browser.</summary>
    public ClipDto AddFromElsewhere(string? text, bool deleted = false, Guid? syncId = null, byte[]? png = null)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            var clip = new ClipDto
            {
                SyncId = syncId ?? Guid.NewGuid(),
                Kind = png is null ? ClipKind.Text : ClipKind.Image,
                Text = png is null ? text : null,
                ContentHash = png is null ? Hash(text ?? "") : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(png)),
                CreatedAt = now,
                CopiedAt = now,
                UpdatedAt = now,
                IsDeleted = deleted,
                HasImage = png is not null,
                ImageWidth = png is null ? null : 10,
                ImageHeight = png is null ? null : 10,
                Source = "Laptop",
                Revision = ++Revision,
            };
            if (png is not null)
            {
                Images[clip.SyncId] = png;
            }

            Clips[clip.SyncId] = clip;
            return clip;
        }
    }

    public void DeleteElsewhere(Guid syncId)
    {
        lock (_gate)
        {
            Clips[syncId] = Clips[syncId] with { IsDeleted = true, Text = null, Revision = ++Revision, UpdatedAt = DateTimeOffset.UtcNow };
        }
    }

    public static string Hash(string text) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);

        if (path == SyncContract.DeviceLogin)
        {
            var login = (await request.Content!.ReadFromJsonAsync<DeviceLoginRequest>(SyncContract.Json, cancellationToken))!;
            if (login.UserName != UserName || login.Password != Password)
            {
                return Json(HttpStatusCode.Unauthorized, new ApiError("Benutzername oder Passwort stimmen nicht."));
            }

            var token = Guid.NewGuid().ToString();
            lock (_gate)
            {
                Tokens.Add(token);
            }

            return Json(HttpStatusCode.OK, new DeviceLoginResponse(token, 7, Account(login.DeviceName)));
        }

        lock (_gate)
        {
            if (request.Headers.Authorization?.Parameter is not { } bearer || !Tokens.Contains(bearer))
            {
                return Json(HttpStatusCode.Unauthorized, new ApiError("Gerät unbekannt."));
            }
        }

        if (path == SyncContract.DeviceLogout)
        {
            lock (_gate)
            {
                Tokens.Remove(request.Headers.Authorization!.Parameter!);
            }

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        if (path == SyncContract.Account)
        {
            if (request.Method == HttpMethod.Put)
            {
                RetentionDays = (await request.Content!.ReadFromJsonAsync<AccountSettingsUpdate>(SyncContract.Json, cancellationToken))!.RetentionDays;
            }

            return Json(HttpStatusCode.OK, Account("Test"));
        }

        if (path == SyncContract.Changes)
        {
            var since = long.Parse(query["since"] ?? "0");
            var limit = int.Parse(query["limit"] ?? "200");
            lock (_gate)
            {
                var all = Clips.Values.Where(clip => clip.Revision > since).OrderBy(clip => clip.Revision).ToList();
                return Json(HttpStatusCode.OK, new ChangeSet(all.Take(limit).ToList(), Revision, all.Count > limit, RetentionDays));
            }
        }

        if (path == SyncContract.Wait)
        {
            return Json(HttpStatusCode.OK, new WaitResult(Revision));
        }

        if (path == SyncContract.Push)
        {
            var push = (await request.Content!.ReadFromJsonAsync<PushRequest>(SyncContract.Json, cancellationToken))!;
            lock (_gate)
            {
                var results = push.Clips.Select(Apply).ToList();
                return Json(HttpStatusCode.OK, new PushResponse(results, Revision));
            }
        }

        if (path.EndsWith("/image", StringComparison.Ordinal))
        {
            var syncId = Guid.Parse(path.Split('/')[^2]);
            if (request.Method == HttpMethod.Put)
            {
                var png = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                lock (_gate)
                {
                    Images[syncId] = png;
                    Clips[syncId] = Clips[syncId] with { HasImage = true, Revision = ++Revision };
                }

                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            lock (_gate)
            {
                return Images.TryGetValue(syncId, out var bytes)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private PushResult Apply(ClipDto pushed)
    {
        if (Clips.TryGetValue(pushed.SyncId, out var existing))
        {
            if (existing.IsDeleted)
            {
                return new PushResult(pushed.SyncId, existing.Revision, null, true, false);
            }

            if (pushed.UpdatedAt < existing.UpdatedAt)
            {
                return new PushResult(pushed.SyncId, existing.Revision, null, false, false);
            }

            var updated = pushed with
            {
                Revision = ++Revision,
                HasImage = existing.HasImage,
                Text = pushed.IsDeleted ? null : pushed.Text,
            };
            Clips[pushed.SyncId] = updated;
            return new PushResult(pushed.SyncId, updated.Revision, null, updated.IsDeleted, updated.Kind == ClipKind.Image && !updated.HasImage && !updated.IsDeleted);
        }

        var same = Clips.Values.FirstOrDefault(clip => !clip.IsDeleted && !pushed.IsDeleted && clip.ContentHash == pushed.ContentHash);
        if (same is not null)
        {
            var moved = same with { CopiedAt = pushed.CopiedAt > same.CopiedAt ? pushed.CopiedAt : same.CopiedAt, Revision = ++Revision };
            Clips[same.SyncId] = moved;
            return new PushResult(pushed.SyncId, moved.Revision, same.SyncId, false, false);
        }

        var created = pushed with { Revision = ++Revision, HasImage = false };
        Clips[pushed.SyncId] = created;
        return new PushResult(pushed.SyncId, created.Revision, null, created.IsDeleted, created.Kind == ClipKind.Image && !created.IsDeleted);
    }

    private AccountInfo Account(string deviceName) => new(UserName, "Anna", RetentionDays, deviceName, "test");

    private static HttpResponseMessage Json<T>(HttpStatusCode status, T body) =>
        new(status) { Content = JsonContent.Create(body, options: SyncContract.Json) };
}
