using SkiaSharp;
using ThixClip.Core.Business;
using ThixClip.Core.Infrastructure;
using ThixClip.Core.Models;
using ThixClip.Shared;

namespace ThixClip.Tests;

/// <summary>The desktop side of the sync protocol against <see cref="FakeCloud"/>.</summary>
public sealed class SyncServiceTests : IDisposable
{
    private const string Server = "https://clip.example.test";

    private readonly TestData _data = new();
    private readonly FakeCloud _cloud = new();

    public SyncServiceTests() => CloudClient.HandlerForTests = () => _cloud;

    public void Dispose() => _data.Dispose();

    [Fact]
    public async Task Connecting_uploads_the_local_history_and_never_stores_the_password()
    {
        ClipService.CaptureText("schon da");
        await ConnectAsync();
        SyncService.Stop();
        await SyncService.SyncOnceAsync();

        var clip = Assert.Single(_cloud.Clips.Values);
        Assert.Equal("schon da", clip.Text);
        Assert.True(SettingsService.Get().Cloud.IsConnected);
        Assert.Equal(_cloud.Revision, SettingsService.Get().Cloud.LastRevision);
        Assert.DoesNotContain(FakeCloud.Password, File.ReadAllText(AppPaths.SettingsFile));
    }

    [Fact]
    public async Task A_wrong_password_is_shown_at_the_password_field()
    {
        var error = await Assert.ThrowsAsync<BusinessException>(() => SyncService.ConnectAsync(Server, FakeCloud.UserName, "falsch", "PC"));
        Assert.Equal("Password", error.Field);
        Assert.False(SettingsService.Get().Cloud.IsConnected);
    }

    [Fact]
    public async Task Changes_and_deletions_from_other_devices_arrive()
    {
        await ConnectAsync();
        var remote = _cloud.AddFromElsewhere("vom Laptop");
        await SyncService.SyncOnceAsync();
        var entry = Assert.Single(ClipService.Query(new ClipQuery()));
        Assert.Equal("vom Laptop", entry.Text);
        Assert.Equal("Laptop", entry.Source);

        _cloud.DeleteElsewhere(remote.SyncId);
        await SyncService.SyncOnceAsync();
        Assert.Empty(ClipService.Query(new ClipQuery()));
    }

    [Fact]
    public async Task A_local_deletion_reaches_the_cloud_and_the_tombstone_goes()
    {
        await ConnectAsync();
        var entry = ClipService.CaptureText("bitte löschen")!;
        await SyncService.SyncOnceAsync();
        Assert.False(_cloud.Clips[entry.SyncId].IsDeleted);

        ClipService.Delete(entry.Id);
        await SyncService.SyncOnceAsync();

        Assert.True(_cloud.Clips[entry.SyncId].IsDeleted);
        Assert.Empty(HistoryStore.LoadAll());
    }

    [Fact]
    public async Task Favourite_changes_go_both_ways()
    {
        await ConnectAsync();
        var entry = ClipService.CaptureText("wichtig")!;
        await SyncService.SyncOnceAsync();

        ClipService.ToggleFavorite(entry.Id);
        await SyncService.SyncOnceAsync();
        Assert.True(_cloud.Clips[entry.SyncId].IsFavorite);

        var remote = _cloud.Clips[entry.SyncId];
        _cloud.Clips[entry.SyncId] = remote with { IsPinned = true, UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(5), Revision = remote.Revision + 100 };
        _cloud.AddFromElsewhere("noch was"); // moves the revision counter past the edit
        await SyncService.SyncOnceAsync();
        Assert.True(ClipService.Get(entry.Id)!.IsPinned);
    }

    [Fact]
    public async Task Images_are_uploaded_and_downloaded()
    {
        await ConnectAsync();
        var local = ClipService.CaptureImage(ImageCodec.Normalize(TestImages.Png(64, 48, SKColors.Orange))!)!;
        await SyncService.SyncOnceAsync();
        Assert.True(_cloud.Images.ContainsKey(local.SyncId));
        Assert.Equal(SyncState.Synced, ClipService.Get(local.Id)!.SyncState);

        var remotePng = ImageCodec.Normalize(TestImages.Png(10, 10, SKColors.Purple))!.Png;
        var remote = _cloud.AddFromElsewhere(null, png: remotePng);
        await SyncService.SyncOnceAsync();
        var arrived = ClipService.Query(new ClipQuery(Section: ClipSection.Images)).Single(entry => entry.SyncId == remote.SyncId);
        Assert.True(arrived.HasImage);
        Assert.Equal(remotePng, ClipService.ReadImage(arrived.Id));
    }

    [Fact]
    public async Task The_same_text_copied_on_two_devices_is_one_entry()
    {
        await ConnectAsync();
        var remote = _cloud.AddFromElsewhere("doppelt");
        ClipService.CaptureText("doppelt");
        await SyncService.SyncOnceAsync();
        await SyncService.SyncOnceAsync();

        var entry = Assert.Single(ClipService.Query(new ClipQuery()));
        Assert.Equal(remote.SyncId, entry.SyncId);
        Assert.Single(_cloud.Clips.Values, clip => !clip.IsDeleted);
    }

    [Fact]
    public async Task The_retention_of_the_account_applies_here()
    {
        await ConnectAsync();
        _cloud.RetentionDays = 7;
        _cloud.AddFromElsewhere("x");
        await SyncService.SyncOnceAsync();
        Assert.Equal(7, SettingsService.Get().RetentionDays);

        await SettingsService.SetRetentionAsync(90);
        Assert.Equal(90, _cloud.RetentionDays);
    }

    [Fact]
    public async Task A_removed_device_is_signed_out_and_keeps_its_history()
    {
        await ConnectAsync();
        ClipService.CaptureText("bleibt");
        _cloud.Tokens.Clear();

        SyncService.Start();
        for (var i = 0; i < 50 && SyncService.Status.State != CloudState.SignedOut; i++)
        {
            await Task.Delay(50);
        }

        Assert.Equal(CloudState.SignedOut, SyncService.Status.State);
        Assert.False(SettingsService.Get().Cloud.IsConnected);
        Assert.Single(ClipService.Query(new ClipQuery()));
    }

    [Fact]
    public async Task The_cloud_policy_blocks_connecting()
    {
        AccessPolicy.UsePolicyReaderForTests(name => name == "DisableCloud");
        var error = await Assert.ThrowsAsync<BusinessException>(() => SyncService.ConnectAsync(Server, FakeCloud.UserName, FakeCloud.Password, "PC"));
        Assert.Contains("DisableCloud", error.Message);
    }

    [Theory]
    [InlineData("clip.example.com", "https://clip.example.com")]
    [InlineData("http://192.168.1.5:4950/", "http://192.168.1.5:4950")]
    [InlineData("https://example.com/thixclip/", "https://example.com/thixclip")]
    public void Server_addresses_are_normalised(string input, string expected) =>
        Assert.Equal(expected, SyncService.NormalizeServerUrl(input));

    [Fact]
    public void Invalid_server_addresses_are_refused() =>
        Assert.Throws<BusinessException>(() => SyncService.NormalizeServerUrl("ftp://example.com"));

    private async Task ConnectAsync()
    {
        await SyncService.ConnectAsync(Server, FakeCloud.UserName, FakeCloud.Password, "Test-PC");
        SyncService.Stop(); // the tests drive the rounds themselves
    }
}
