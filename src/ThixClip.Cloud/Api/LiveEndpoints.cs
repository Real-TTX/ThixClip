using ThixClip.Cloud.Business;
using ThixClip.Cloud.Data;
using ThixClip.Cloud.Web;
using ThixClip.Shared;

namespace ThixClip.Cloud.Api;

/// <summary>
/// What the pages fetch with the cookie of the browser: the long poll of the live list (<c>/live/revision</c>) and the pictures of the clips.
/// Without a session they answer 401 (never the sign-in page).
/// </summary>
public static class LiveEndpoints
{
    public static void MapLiveEndpoints(this WebApplication app)
    {
        var live = app.MapGroup("/live").RequireAuthorization();

        // The list in the browser waits here for the next change of the account (another PC, a phone, the retention) and reloads itself.
        live.MapGet("/revision", async (HttpContext http, AppDbContext db, CurrentUser me, long? since, int? timeout) =>
        {
            var seconds = Math.Clamp(timeout ?? 25, 1, SyncContract.MaxWaitSeconds);
            http.Response.Headers.CacheControl = "no-store";
            try
            {
                var latest = await ClipService.WaitAsync(db, me.Actor, since ?? 0, TimeSpan.FromSeconds(seconds), http.RequestAborted);
                return Results.Json(new { latest });
            }
            catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
            {
                return Results.Empty;
            }
        });

        live.MapGet("/clips/{id:long}/image", async (HttpContext http, AppDbContext db, CurrentUser me, long id, bool? download) =>
        {
            var bytes = await ClipService.ImageAsync(db, me.Actor, id, thumbnail: false);
            if (bytes is null)
            {
                return Results.NotFound();
            }

            http.Response.Headers.CacheControl = "private, max-age=3600";
            return download == true
                ? Results.File(bytes, "image/png", $"ThixClip-{id}.png")
                : Results.File(bytes, "image/png");
        });

        live.MapGet("/clips/{id:long}/thumbnail", async (HttpContext http, AppDbContext db, CurrentUser me, long id) =>
        {
            var bytes = await ClipService.ImageAsync(db, me.Actor, id, thumbnail: true);
            if (bytes is null)
            {
                return Results.NotFound();
            }

            // A picture of a clip never changes (a new picture is a new clip): the browser and the service worker may keep it.
            http.Response.Headers.CacheControl = "private, max-age=86400";
            return Results.File(bytes, "image/png");
        });
    }
}
