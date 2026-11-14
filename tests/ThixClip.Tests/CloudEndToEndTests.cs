using SkiaSharp;
using ThixClip.Core.Business;
using ThixClip.Core.Infrastructure;
using ThixClip.Core.Models;
using ThixClip.Shared;

namespace ThixClip.Tests;

/// <summary>
/// The desktop app against a real, running ThixClip.Cloud: this computer (SyncService) and a second device (the API
/// directly) share one account. Runs only when THIXCLIP_CLOUD_URL, THIXCLIP_CLOUD_USER and THIXCLIP_CLOUD_PASSWORD are
/// set, e.g. against the local stack (docker-compose.local.yml, test account in docs/CLOUD.md).
/// </summary>
[Trait("Category", "E2E")]
public sealed class CloudEndToEndTests : IDisposable
{
    private static readonly string? Url = Environment.GetEnvironmentVariable("THIXCLIP_CLOUD_URL");
    private static readonly string? User = Environment.GetEnvironmentVariable("THIXCLIP_CLOUD_USER");
    private static readonly string? Password = Environment.GetEnvironmentVariable("THIXCLIP_CLOUD_PASSWORD");

    private readonly TestData _data = new();

    public void Dispose() => _data.Dispose();

    [Fact]
    public async Task Two_devices_share_texts_images_favourites_and_deletions()
    {
        if (Url is null || User is null || Password is null)
        {
            return; // no cloud configured: nothing to check here
        }

        var marker = Guid.NewGuid().ToString("N")[..8];

        // This computer signs in and records a text and a picture.
        await SyncService.ConnectAsync(Url, User, Password, $"E2E-Desktop-{marker}");
        SyncService.Stop();
        var text = ClipService.CaptureText($"E2E {marker}: vom Desktop")!;
        var image = ClipService.CaptureImage(ImageCodec.Normalize(TestImages.Png(320, 200, SKColors.MediumSeaGreen))!)!;
        await SyncService.SyncOnceAsync();
        Assert.Equal(SyncState.Synced, ClipService.Get(text.Id)!.SyncState);
        Assert.Equal(SyncState.Synced, ClipService.Get(image.Id)!.SyncState);

        // A second device sees both, image included.
        using var anonymous = CloudClient.Create(Url, null);
        var login = await anonymous.LoginAsync(new DeviceLoginRequest(User, Password, $"E2E-Laptop-{marker}", "Test", "test"), CancellationToken.None);
        using var laptop = CloudClient.Create(Url, login.Token);
        var all = await AllChangesAsync(laptop);
        var seenText = all.Single(clip => clip.SyncId == text.SyncId);
        Assert.Equal($"E2E {marker}: vom Desktop", seenText.Text);
        var seenImage = all.Single(clip => clip.SyncId == image.SyncId);
        Assert.True(seenImage.HasImage);
        Assert.Equal(ClipService.ReadImage(image.Id), await laptop.DownloadImageAsync(image.SyncId, CancellationToken.None));

        // The second device adds a text, marks the picture as favourite and deletes the desktop's text.
        var now = DateTimeOffset.UtcNow.AddSeconds(1);
        var fromLaptop = new ClipDto
        {
            SyncId = Guid.NewGuid(),
            Kind = ClipKind.Text,
            Text = $"E2E {marker}: vom Laptop",
            ContentHash = ClipService.HashOf(System.Text.Encoding.UTF8.GetBytes($"E2E {marker}: vom Laptop")),
            CreatedAt = now,
            CopiedAt = now,
            UpdatedAt = now,
        };
        await laptop.PushAsync(new PushRequest(
        [
            fromLaptop,
            seenImage with { IsFavorite = true, UpdatedAt = now },
            seenText with { IsDeleted = true, Text = null, UpdatedAt = now },
        ]), CancellationToken.None);

        // The long poll of this computer wakes up, the next round brings everything here.
        var waited = await laptop.WaitAsync(0, 1, CancellationToken.None);
        Assert.True(waited.Latest > 0);
        await SyncService.SyncOnceAsync();
        var history = ClipService.Query(new ClipQuery(Search: marker));
        Assert.Contains(history, entry => entry.Text == $"E2E {marker}: vom Laptop");
        Assert.DoesNotContain(history, entry => entry.SyncId == text.SyncId);
        Assert.True(ClipService.Query(new ClipQuery(Section: ClipSection.Images)).Single(entry => entry.SyncId == image.SyncId).IsFavorite);

        // Copying the laptop's text here again is the same entry (no duplicate in the cloud).
        ClipService.CaptureText($"E2E {marker}: vom Laptop");
        await SyncService.SyncOnceAsync();
        var cloudCopies = (await AllChangesAsync(laptop)).Where(clip => clip.Text == $"E2E {marker}: vom Laptop" && !clip.IsDeleted).ToList();
        Assert.Single(cloudCopies);

        // Clean up: delete what the test made, sign both devices out.
        foreach (var entry in ClipService.Query(new ClipQuery()))
        {
            ClipService.Delete(entry.Id);
        }

        await SyncService.SyncOnceAsync();
        await laptop.LogoutAsync(CancellationToken.None);
        await SyncService.DisconnectAsync();
        Assert.False(SettingsService.Get().Cloud.IsConnected);
    }

    private static async Task<List<ClipDto>> AllChangesAsync(CloudClient client)
    {
        var result = new Dictionary<Guid, ClipDto>();
        long since = 0;
        while (true)
        {
            var changes = await client.GetChangesAsync(since, SyncContract.MaxBatchSize, CancellationToken.None);
            foreach (var clip in changes.Clips)
            {
                result[clip.SyncId] = clip;
            }

            since = changes.Clips.Count > 0 ? changes.Clips.Max(clip => clip.Revision) : changes.Latest;
            if (!changes.HasMore)
            {
                return [.. result.Values];
            }
        }
    }
}
