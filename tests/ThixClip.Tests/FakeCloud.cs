using System.Net;
using System.Net.Http.Json;
using MatCloud.Contract;
using MatCloud.Contract.Clip;

namespace ThixClip.Tests;

/// <summary>
/// MatCloud with its clip module in memory, after the rules of <see cref="CloudContract"/> and <see cref="ClipContract"/>: the sign-in
/// (password, or in the browser: <see cref="ApproveLink"/> plays the person who confirms the code), one revision counter, tombstones,
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

    public int RetentionDays { get; set; } = ClipContract.DefaultRetentionDays;

    /// <summary>The sign-ins in the browser by code ("KXQ0-7MPA").</summary>
    public Dictionary<string, Link> Links { get; } = [];

    /// <summary>The User-Agent of the last request.</summary>
    public string? LastUserAgent { get; private set; }

    /// <summary>The person confirms the code in the browser (signed in as <see cref="UserName"/>).</summary>
    public void ApproveLink(string code) => SetLinkState(code, DeviceLinkState.Approved);

    /// <summary>The person refuses the code in the browser.</summary>
    public void DenyLink(string code) => SetLinkState(code, DeviceLinkState.Denied);

    /// <summary>The code got too old.</summary>
    public void ExpireLink(string code) => SetLinkState(code, DeviceLinkState.Expired);

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
        LastUserAgent = request.Headers.UserAgent.ToString();
        if (!request.Headers.TryGetValues(CloudContract.ProtocolHeader, out var protocol) || protocol.Single() != CloudContract.ProtocolVersion.ToString())
        {
            return Json(HttpStatusCode.BadRequest, new ApiError("Das Protokoll fehlt."));
        }

        if (path == CloudContract.DeviceLink)
        {
            var start = (await request.Content!.ReadFromJsonAsync<DeviceLinkRequest>(CloudContract.Json, cancellationToken))!;
            if (start.App != CloudApps.Clip)
            {
                return Json(HttpStatusCode.BadRequest, new ApiError("Diese App kennt die MatCloud nicht.", "app"));
            }

            lock (_gate)
            {
                var code = $"KXQ{Links.Count}-7MPA";
                var link = new Link(code, Guid.NewGuid().ToString(), start.DeviceName);
                Links[code] = link;
                return Json(HttpStatusCode.OK, new DeviceLinkStart(code, $"https://cloud.example.test/Connect?code={code}", link.PollToken, 600, 1));
            }
        }

        if (path == CloudContract.DeviceLinkPoll)
        {
            var poll = (await request.Content!.ReadFromJsonAsync<DeviceLinkPollRequest>(CloudContract.Json, cancellationToken))!;
            lock (_gate)
            {
                if (Links.Values.FirstOrDefault(candidate => candidate.PollToken == poll.PollToken) is not { } link)
                {
                    return Json(HttpStatusCode.OK, new DeviceLinkPollResponse(DeviceLinkState.Expired));
                }

                if (link.State != DeviceLinkState.Approved)
                {
                    return Json(HttpStatusCode.OK, new DeviceLinkPollResponse(link.State));
                }

                // The token comes once; then the code is used up.
                Links[link.Code] = link with { State = DeviceLinkState.Expired };
                var token = Guid.NewGuid().ToString();
                Tokens.Add(token);
                return Json(HttpStatusCode.OK, new DeviceLinkPollResponse(DeviceLinkState.Approved, new DeviceLoginResponse(token, 8, Account(link.DeviceName))));
            }
        }

        if (path == CloudContract.DeviceLogin)
        {
            var login = (await request.Content!.ReadFromJsonAsync<DeviceLoginRequest>(CloudContract.Json, cancellationToken))!;
            if (login.App != CloudApps.Clip)
            {
                return Json(HttpStatusCode.BadRequest, new ApiError("Diese App kennt die MatCloud nicht.", "app"));
            }

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

        if (path == CloudContract.DeviceLogout)
        {
            lock (_gate)
            {
                Tokens.Remove(request.Headers.Authorization!.Parameter!);
            }

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        if (path == CloudContract.Account)
        {
            return request.Method == HttpMethod.Get ? Json(HttpStatusCode.OK, Account("Test")) : new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        }

        if (path == ClipContract.Settings)
        {
            if (request.Method == HttpMethod.Put)
            {
                var update = (await request.Content!.ReadFromJsonAsync<ClipSettings>(CloudContract.Json, cancellationToken))!;
                if (!ClipContract.IsValidRetention(update.RetentionDays))
                {
                    return Json(HttpStatusCode.BadRequest, new ApiError("Diese Speicherdauer gibt es nicht.", "retentionDays"));
                }

                RetentionDays = update.RetentionDays;
            }

            return Json(HttpStatusCode.OK, new ClipSettings(RetentionDays));
        }

        if (path == ClipContract.Changes)
        {
            var since = long.Parse(query["since"] ?? "0");
            var limit = int.Parse(query["limit"] ?? "200");
            lock (_gate)
            {
                var all = Clips.Values.Where(clip => clip.Revision > since).OrderBy(clip => clip.Revision).ToList();
                return Json(HttpStatusCode.OK, new ClipChangeSet(all.Take(limit).ToList(), Revision, all.Count > limit, RetentionDays));
            }
        }

        if (path == ClipContract.Wait)
        {
            // A real long poll: answers once there is something above "since" (or after the timeout), so a running loop idles.
            var since = long.Parse(query["since"] ?? "0");
            var until = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(int.Parse(query["timeout"] ?? "0"), 0, CloudContract.MaxWaitSeconds));
            while (Revision <= since && DateTimeOffset.UtcNow < until)
            {
                await Task.Delay(50, cancellationToken);
            }

            return Json(HttpStatusCode.OK, new WaitResult(Revision));
        }

        if (path == ClipContract.Push)
        {
            var push = (await request.Content!.ReadFromJsonAsync<ClipPushRequest>(CloudContract.Json, cancellationToken))!;
            lock (_gate)
            {
                var results = push.Clips.Select(Apply).ToList();
                return Json(HttpStatusCode.OK, new ClipPushResponse(results, Revision));
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

    private ClipPushResult Apply(ClipDto pushed)
    {
        if (Clips.TryGetValue(pushed.SyncId, out var existing))
        {
            if (existing.IsDeleted)
            {
                return new ClipPushResult(pushed.SyncId, existing.Revision, null, true, false);
            }

            if (pushed.UpdatedAt < existing.UpdatedAt)
            {
                return new ClipPushResult(pushed.SyncId, existing.Revision, null, false, false);
            }

            var updated = pushed with
            {
                Revision = ++Revision,
                HasImage = existing.HasImage,
                Text = pushed.IsDeleted ? null : pushed.Text,
            };
            Clips[pushed.SyncId] = updated;
            return new ClipPushResult(pushed.SyncId, updated.Revision, null, updated.IsDeleted, updated.Kind == ClipKind.Image && !updated.HasImage && !updated.IsDeleted);
        }

        var same = Clips.Values.FirstOrDefault(clip => !clip.IsDeleted && !pushed.IsDeleted && clip.ContentHash == pushed.ContentHash);
        if (same is not null)
        {
            var moved = same with { CopiedAt = pushed.CopiedAt > same.CopiedAt ? pushed.CopiedAt : same.CopiedAt, Revision = ++Revision };
            Clips[same.SyncId] = moved;
            return new ClipPushResult(pushed.SyncId, moved.Revision, same.SyncId, false, false);
        }

        var created = pushed with { Revision = ++Revision, HasImage = false };
        Clips[pushed.SyncId] = created;
        return new ClipPushResult(pushed.SyncId, created.Revision, null, created.IsDeleted, created.Kind == ClipKind.Image && !created.IsDeleted);
    }

    private AccountInfo Account(string deviceName) => new(UserName, "Anna", deviceName, CloudApps.Clip, "test");

    private void SetLinkState(string code, DeviceLinkState state)
    {
        lock (_gate)
        {
            Links[code] = Links[code] with { State = state };
        }
    }

    private static HttpResponseMessage Json<T>(HttpStatusCode status, T body) =>
        new(status) { Content = JsonContent.Create(body, options: CloudContract.Json) };
}

/// <summary>A sign-in in the browser as <see cref="FakeCloud"/> keeps it.</summary>
internal sealed record Link(string Code, string PollToken, string DeviceName, DeviceLinkState State = DeviceLinkState.Pending);
