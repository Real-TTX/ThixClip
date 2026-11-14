using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ThixClip.Cloud.Business;
using ThixClip.Cloud.Data;
using ThixClip.Cloud.Web;

namespace ThixClip.Cloud;

/// <summary>Docker <c>HEALTHCHECK</c> without curl: the app asks its own <c>/health</c> and answers with an exit code.</summary>
public static partial class HealthCheck
{
    [GeneratedRegex(@":(\d+)")]
    private static partial Regex PortRegex();

    public static async Task<int> RunAsync()
    {
        var urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        var match = PortRegex().Match(urls ?? string.Empty);
        var port = match.Success ? match.Groups[1].Value : "4950";

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var response = await http.GetAsync($"http://localhost:{port}/health");
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return 1;
        }
    }
}

public static class Database
{
    /// <summary>Creates and updates the tables. The database container may still be starting: try again for a while before giving up.</summary>
    public static async Task MigrateWithRetryAsync(WebApplication app)
    {
        const int attempts = 30;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var scope = app.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await db.Database.MigrateAsync();
                app.Logger.LogInformation("The database is up to date.");
                return;
            }
            catch (Exception ex) when (attempt < attempts)
            {
                app.Logger.LogWarning("The database is not ready yet (attempt {Attempt}/{Attempts}): {Message}", attempt, attempts, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
        }
    }
}

/// <summary>Whether the app has an account yet. Without one, every page leads to the setup assistant.</summary>
public sealed class SetupState
{
    private volatile bool _done;

    public async Task<bool> NeedsSetupAsync(AppDbContext db)
    {
        if (_done)
        {
            return false;
        }

        var needs = await UserService.NeedsSetupAsync(db);
        _done = !needs;
        return needs;
    }

    public void Done() => _done = true;
}

/// <summary>On a fresh database the first visit goes to the setup assistant, which creates the first administrator.</summary>
public sealed class SetupRedirect(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, SetupState state, AppDbContext db)
    {
        var path = context.Request.Path;
        if (!IsOpen(path) && await state.NeedsSetupAsync(db))
        {
            context.Response.Redirect("/Account/Setup");
            return;
        }

        await next(context);
    }

    private static bool IsOpen(PathString path)
        => path.StartsWithSegments("/Account/Setup") || path.StartsWithSegments("/health") || path.StartsWithSegments("/Error") || path.StartsWithSegments("/api")
           || StaticFiles.IsStatic(path);
}

/// <summary>A person whose password was set by an administrator chooses their own before anything else.</summary>
public sealed class ForcePasswordChange(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, CurrentUser current)
    {
        var path = context.Request.Path;
        if (current.Snapshot is { MustChangePassword: true }
            && !path.StartsWithSegments("/Account") && !path.StartsWithSegments("/health") && !path.StartsWithSegments("/api") && !StaticFiles.IsStatic(path))
        {
            context.Response.Redirect("/Account/Security?force=1");
            return;
        }

        await next(context);
    }
}

/// <summary>Headers every response carries: no inline script or style (the toolkit needs none), nothing from another site, no guessing of types.</summary>
public sealed class SecurityHeaders(RequestDelegate next)
{
    private const string Policy =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data: blob:; font-src 'self'; connect-src 'self'; worker-src 'self'; manifest-src 'self'; " +
        "frame-src 'none'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

    public Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers["Content-Security-Policy"] = Policy;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "same-origin";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), clipboard-write=(self)";
        return next(context);
    }
}

/// <summary>The files of wwwroot (the toolkit, styles, scripts, icons, the PWA files).</summary>
public static class StaticFiles
{
    public static bool IsStatic(PathString path)
        => path.StartsWithSegments("/matui") || path.StartsWithSegments("/css") || path.StartsWithSegments("/js") || path.StartsWithSegments("/img")
           || path == "/favicon.ico" || path == "/manifest.webmanifest" || path == "/sw.js" || path == "/offline.html" || path == "/robots.txt" || path == "/llms.txt";
}

/// <summary>
/// The housekeeping that runs by itself every hour: ended sessions go, and every account's retention removes the clips it no longer keeps
/// (as tombstones, so the devices remove them too).
/// </summary>
public sealed class MaintenanceService(IServiceScopeFactory scopes, ILogger<MaintenanceService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "The housekeeping failed; it tries again later.");
                }

                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // the app stops
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var sessions = await SessionService.CleanUpAsync(db, ct);
        var clips = await ClipService.ApplyRetentionForAllAsync(db, ct);
        if (sessions + clips > 0)
        {
            logger.LogInformation("Housekeeping: {Sessions} old sessions removed, {Clips} clips past their retention deleted.", sessions, clips);
        }
    }
}

/// <summary>A text that is posted empty is an empty string, not <c>null</c>: the properties of the page models are not nullable.</summary>
public sealed class EmptyStringBinderProvider : Microsoft.AspNetCore.Mvc.ModelBinding.IModelBinderProvider
{
    public Microsoft.AspNetCore.Mvc.ModelBinding.IModelBinder? GetBinder(Microsoft.AspNetCore.Mvc.ModelBinding.ModelBinderProviderContext context)
        => context.Metadata.ModelType == typeof(string) ? new EmptyStringBinder() : null;

    private sealed class EmptyStringBinder : Microsoft.AspNetCore.Mvc.ModelBinding.IModelBinder
    {
        public Task BindModelAsync(Microsoft.AspNetCore.Mvc.ModelBinding.ModelBindingContext bindingContext)
        {
            var result = bindingContext.ValueProvider.GetValue(bindingContext.ModelName);
            if (result == Microsoft.AspNetCore.Mvc.ModelBinding.ValueProviderResult.None)
            {
                return Task.CompletedTask;
            }

            bindingContext.ModelState.SetModelValue(bindingContext.ModelName, result);
            bindingContext.Result = Microsoft.AspNetCore.Mvc.ModelBinding.ModelBindingResult.Success(result.FirstValue ?? string.Empty);
            return Task.CompletedTask;
        }
    }
}
