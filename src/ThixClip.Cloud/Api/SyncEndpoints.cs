using System.Globalization;
using ThixClip.Cloud.Business;
using ThixClip.Cloud.Data;
using ThixClip.Shared;

namespace ThixClip.Cloud.Api;

/// <summary>
/// The API of the desktop apps (<see cref="SyncContract"/>). Every route but the login needs the device token as a bearer token. Answers are JSON
/// (camelCase, enums as text); an error is an <see cref="ApiError"/> with a German message. Every answer carries the protocol version
/// (<see cref="SyncContract.ProtocolHeader"/>); a device that speaks a newer protocol than the server gets 400.
/// </summary>
public static class SyncEndpoints
{
    public const string DeviceItem = "ThixClip.Device";

    public static void MapSyncApi(this WebApplication app)
    {
        var api = app.MapGroup(string.Empty).AllowAnonymous().DisableAntiforgery().AddEndpointFilter(ProtocolAsync);
        api.MapPost(SyncContract.DeviceLogin, LoginAsync).RequireRateLimiting("signin");

        var device = api.MapGroup(string.Empty).AddEndpointFilter(DeviceAsync);
        device.MapPost(SyncContract.DeviceLogout, async (HttpContext http, AppDbContext db) =>
        {
            await DeviceService.LogoutAsync(db, ActorOf(http));
            return Results.NoContent();
        });
        device.MapGet(SyncContract.Account, async (HttpContext http, AppDbContext db) => Answer(await AccountService.InfoAsync(db, ActorOf(http), AppInfo.Version)));
        device.MapPut(SyncContract.Account, async (HttpContext http, AppDbContext db, AccountSettingsUpdate? update) =>
            update is null
                ? Error(StatusCodes.Status400BadRequest, "Die Anfrage enthält keine Einstellungen.")
                : Answer(await AccountService.SetRetentionAsync(db, ActorOf(http), update.RetentionDays, AppInfo.Version)));
        device.MapGet(SyncContract.Changes, async (HttpContext http, AppDbContext db, long? since, int? limit) =>
            Json(await SyncService.ChangesAsync(db, ActorOf(http), since ?? 0, limit ?? SyncService.DefaultLimit, http.RequestAborted)));
        device.MapGet(SyncContract.Wait, WaitAsync);
        device.MapPost(SyncContract.Push, async (HttpContext http, AppDbContext db, PushRequest? request) =>
        {
            if (SyncService.Validate(request) is { } problem)
            {
                return Error(StatusCodes.Status400BadRequest, problem.Message, problem.Field);
            }

            return Json(await SyncService.PushAsync(db, ActorOf(http), request!, http.RequestAborted));
        });
        device.MapPut("/api/v1/clips/{syncId:guid}/image", UploadImageAsync);
        device.MapGet("/api/v1/clips/{syncId:guid}/image", async (HttpContext http, AppDbContext db, Guid syncId) => Png(await SyncService.ImageAsync(db, ActorOf(http), syncId, false, http.RequestAborted)));
        device.MapGet("/api/v1/clips/{syncId:guid}/thumbnail", async (HttpContext http, AppDbContext db, Guid syncId) => Png(await SyncService.ImageAsync(db, ActorOf(http), syncId, true, http.RequestAborted)));
    }

    public static IResult Error(int status, string message, string? field = null) => Results.Json(new ApiError(message, field), SyncContract.Json, statusCode: status);

    private static async Task<IResult> LoginAsync(HttpContext http, AppDbContext db, DeviceLoginRequest? request)
    {
        if (request is null)
        {
            return Error(StatusCodes.Status400BadRequest, "Die Anmeldung braucht Benutzername, Passwort und Gerätename.");
        }

        var outcome = await DeviceService.LoginAsync(db, request, http.Connection.RemoteIpAddress?.ToString(), AppInfo.Version);
        if (outcome.Ok)
        {
            return Json(outcome.Value!);
        }

        // A wrong name or password is 401; a missing device name is a request the app has to fix (400).
        var status = outcome.Field == "password" ? StatusCodes.Status401Unauthorized : StatusCodes.Status400BadRequest;
        return Error(status, outcome.Error!, outcome.Field);
    }

    private static async Task<IResult> WaitAsync(HttpContext http, AppDbContext db, long? since, int? timeout)
    {
        var seconds = Math.Clamp(timeout ?? 25, 1, SyncContract.MaxWaitSeconds);
        try
        {
            var latest = await ClipService.WaitAsync(db, ActorOf(http), since ?? 0, TimeSpan.FromSeconds(seconds), http.RequestAborted);
            return Json(new WaitResult(latest));
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            return Results.Empty;
        }
    }

    private static async Task<IResult> UploadImageAsync(HttpContext http, AppDbContext db, Guid syncId)
    {
        var bytes = await ReadBodyAsync(http.Request, SyncContract.MaxImageBytes, http.RequestAborted);
        if (bytes is null)
        {
            return Error(StatusCodes.Status413PayloadTooLarge, $"Das Bild ist größer als {SyncContract.MaxImageBytes / 1024 / 1024} MB.", "image");
        }

        var outcome = await SyncService.UploadImageAsync(db, ActorOf(http), syncId, bytes, http.RequestAborted);
        if (outcome.NotFound)
        {
            return Error(StatusCodes.Status404NotFound, "Diesen Eintrag gibt es nicht (mehr).");
        }

        return outcome.Ok ? Json(new { revision = outcome.Value }) : Error(StatusCodes.Status400BadRequest, outcome.Error!, outcome.Field);
    }

    /// <summary>The body of a request, at most <paramref name="limit"/> bytes; null when it is longer.</summary>
    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, long limit, CancellationToken ct)
    {
        if (request.ContentLength > limit)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>Every answer names the protocol of the server; a device with a newer protocol is told to wait for a server update.</summary>
    private static async ValueTask<object?> ProtocolAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        http.Response.Headers[SyncContract.ProtocolHeader] = SyncContract.ProtocolVersion.ToString(CultureInfo.InvariantCulture);
        var asked = http.Request.Headers[SyncContract.ProtocolHeader].ToString();
        if (int.TryParse(asked, NumberStyles.Integer, CultureInfo.InvariantCulture, out var version) && version > SyncContract.ProtocolVersion)
        {
            return Error(StatusCodes.Status400BadRequest, "Diese Version von ThixClip ist neuer als der Server. Bitte ThixClip.Cloud aktualisieren.");
        }

        return await next(context);
    }

    /// <summary>The device of the bearer token, or 401.</summary>
    private static async ValueTask<object?> DeviceAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var header = http.Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header["Bearer ".Length..].Trim() : null;
        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var device = await DeviceService.AuthenticateAsync(db, token, http.RequestAborted);
        if (device is null)
        {
            http.Response.Headers.WWWAuthenticate = "Bearer";
            return Error(StatusCodes.Status401Unauthorized, "Dieses Gerät ist nicht (mehr) angemeldet. Bitte in ThixClip erneut anmelden.");
        }

        http.Items[DeviceItem] = device;
        http.Items[AppDbContext.ActorItem] = device.UserId;
        await DeviceService.TouchAsync(db, device, http.Connection.RemoteIpAddress?.ToString(), AppVersionOf(http.Request), http.RequestAborted);
        return await next(context);
    }

    /// <summary>The version of the app from its user agent ("ThixClip/0.1.42-20261114 (windows)").</summary>
    private static string? AppVersionOf(HttpRequest request)
    {
        var agent = request.Headers.UserAgent.ToString();
        const string prefix = "ThixClip/";
        if (!agent.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var version = agent[prefix.Length..];
        var end = version.IndexOf(' ');
        return end < 0 ? version : version[..end];
    }

    private static Actor ActorOf(HttpContext http) => ((DeviceIdentity)http.Items[DeviceItem]!).Actor;

    private static IResult Json<T>(T value) => Results.Json(value, SyncContract.Json);

    private static IResult Answer<T>(Outcome<T> outcome)
    {
        if (outcome.NotFound)
        {
            return Error(StatusCodes.Status404NotFound, "Nicht gefunden.");
        }

        return outcome.Ok ? Json(outcome.Value) : Error(StatusCodes.Status400BadRequest, outcome.Error!, outcome.Field);
    }

    private static IResult Png(byte[]? bytes)
        => bytes is null
            ? Error(StatusCodes.Status404NotFound, "Dieses Bild gibt es nicht (mehr) oder es wurde noch nicht hochgeladen.")
            : Results.File(bytes, "image/png");
}
