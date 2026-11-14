using Microsoft.EntityFrameworkCore;
using ThixClip.Cloud.Business;
using ThixClip.Cloud.Data;
using ThixClip.Shared;

namespace ThixClip.Cloud.Tests;

/// <summary>The sync rules of the business layer against a real PostgreSQL (THIXCLIP_TEST_DB).</summary>
public sealed class SyncTests
{
    private static Actor Device(User user, string name = "PC-1") => new(user.Id, user.IsAdmin, 1, name);

    [DbFact]
    public async Task APushCreatesClipsAndChangesReturnThemInRevisionOrder()
    {
        await using var test = await TestDatabase.CreateAsync();
        var anna = await test.AddUserAsync("anna");
        var pc = Device(anna);

        var first = Samples.Text("Produktmeeting morgen um 10 Uhr");
        var second = Samples.Text("https://www.example.com/produkt");
        var response = await SyncService.PushAsync(test.Db, pc, new PushRequest([first, second]));

        Assert.Equal(2, response.Results.Count);
        Assert.All(response.Results, r => Assert.True(r.Revision > 0));
        Assert.Equal(response.Results.Max(r => r.Revision), response.Latest);

        var changes = await SyncService.ChangesAsync(test.NewContext(), pc, 0, 100);
        Assert.Equal([first.SyncId, second.SyncId], changes.Clips.Select(c => c.SyncId));
        Assert.Equal(ClipKind.Link, changes.Clips[1].Kind);
        Assert.Equal(changes.Clips[^1].Revision, changes.Latest);
        Assert.False(changes.HasMore);
        Assert.Equal(SyncContract.DefaultRetentionDays, changes.RetentionDays);

        var nothing = await SyncService.ChangesAsync(test.NewContext(), pc, changes.Latest, 100);
        Assert.Empty(nothing.Clips);
        Assert.Equal(changes.Latest, nothing.Latest);
    }

    [DbFact]
    public async Task ChangesComeInPages()
    {
        await using var test = await TestDatabase.CreateAsync();
        var anna = await test.AddUserAsync("anna");
        var pc = Device(anna);
        await SyncService.PushAsync(test.Db, pc, new PushRequest(Enumerable.Range(0, 5).Select(i => Samples.Text("clip " + i)).ToList()));

        var page = await SyncService.ChangesAsync(test.NewContext(), pc, 0, 2);
        Assert.Equal(2, page.Clips.Count);
        Assert.True(page.HasMore);
        var rest = await SyncService.ChangesAsync(test.NewContext(), pc, page.Latest, 10);
        Assert.Equal(3, rest.Clips.Count);
        Assert.False(rest.HasMore);
    }

    [DbFact]
    public async Task TheSameContentUnderANewSyncIdMergesIntoTheExistingClip()
    {
        await using var test = await TestDatabase.CreateAsync();
        var anna = await test.AddUserAsync("anna");
        var original = Samples.Text("same text", DateTimeOffset.UtcNow.AddHours(-2));
        await SyncService.PushAsync(test.Db, Device(anna, "PC-1"), new PushRequest([original]));

        var copiedElsewhere = Samples.Text("same text", DateTimeOffset.UtcNow.AddMinutes(-1), favorite: true);
        var response = await SyncService.PushAsync(test.NewContext(), Device(anna, "Mac"), new PushRequest([copiedElsewhere]));

        var result = Assert.Single(response.Results);
        Assert.Equal(original.SyncId, result.MergedInto);
        var clips = await test.NewContext().Clips.Where(c => c.UserId == anna.Id).ToListAsync();
        var clip = Assert.Single(clips);
        Assert.True(clip.IsFavorite);
        Assert.True(clip.CopiedDate > DateTime.UtcNow.AddMinutes(-5), "the clip moved to the top");
    }

    [DbFact]
    public async Task TheNewerChangeWinsAndAnOlderOneIsIgnored()
    {
        await using var test = await TestDatabase.CreateAsync();
        var anna = await test.AddUserAsync("anna");
        var pc = Device(anna);
        var clip = Samples.Text("note", DateTimeOffset.UtcNow.AddMinutes(-10));
        await SyncService.PushAsync(test.Db, pc, new PushRequest([clip]));

        var newer = clip with { IsFavorite = true, UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
        await SyncService.PushAsync(test.NewContext(), pc, new PushRequest([newer]));
        var older = clip with { IsPinned = true, IsFavorite = false, UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5) };
        await SyncService.PushAsync(test.NewContext(), pc, new PushRequest([older]));

        var stored = await test.NewContext().Clips.SingleAsync(c => c.SyncId == clip.SyncId);
        Assert.True(stored.IsFavorite);
        Assert.False(stored.IsPinned);
    }

    [DbFact]
    public async Task DeletingWinsAndIsSyncedAsATombstone()
    {
        await using var test = await TestDatabase.CreateAsync();
        var anna = await test.AddUserAsync("anna");
        var pc = Device(anna);
        var clip = Samples.Text("secret", DateTimeOffset.UtcNow.AddMinutes(-10));
        await SyncService.PushAsync(test.Db, pc, new PushRequest([clip]));
        var before = await SyncService.ChangesAsync(test.NewContext(), pc, 0, 100);

        // deleted in the browser
        var id = await test.NewContext().Clips.Where(c => c.SyncId == clip.SyncId).Select(c => c.Id).SingleAsync();
        Assert.True((await ClipService.DeleteAsync(test.NewContext(), new Actor(anna.Id, false), id)).Ok);

        var changes = await SyncService.ChangesAsync(test.NewContext(), pc, before.Latest, 100);
        var tombstone = Assert.Single(changes.Clips);
        Assert.True(tombstone.IsDeleted);
        Assert.Null(tombstone.Text);
        Assert.Equal(clip.SyncId, tombstone.SyncId);

        // a later change from a device does not bring it back
        var late = clip with { IsFavorite = true, UpdatedAt = DateTimeOffset.UtcNow };
        var response = await SyncService.PushAsync(test.NewContext(), pc, new PushRequest([late]));
        Assert.True(Assert.Single(response.Results).IsDeleted);
        Assert.Empty((await ClipService.ListAsync(test.NewContext(), new Actor(anna.Id, false), new ClipQuery(null, null, null, "copied", true, 1, 50))).Items);
    }

    [DbFact]
    public async Task AnImageClipAsksForItsPngAndServesItAfterTheUpload()
    {
        await using var test = await TestDatabase.CreateAsync();
        var anna = await test.AddUserAsync("anna");
        var pc = Device(anna);
        var png = Samples.Picture(640, 480);
        var clip = Samples.Image(png, ocr: "To-do: Konzept finalisieren");

        var pushed = await SyncService.PushAsync(test.Db, pc, new PushRequest([clip]));
        Assert.True(Assert.Single(pushed.Results).NeedsImage);
        Assert.Null(await SyncService.ImageAsync(test.NewContext(), pc, clip.SyncId, thumbnail: false));

        var upload = await SyncService.UploadImageAsync(test.NewContext(), pc, clip.SyncId, png);
        Assert.True(upload.Ok, upload.Error);
        Assert.True(upload.Value > pushed.Latest, "the upload is a change of its own");

        Assert.Equal(png, await SyncService.ImageAsync(test.NewContext(), pc, clip.SyncId, thumbnail: false));
        Assert.NotNull(await SyncService.ImageAsync(test.NewContext(), pc, clip.SyncId, thumbnail: true));
        var changes = await SyncService.ChangesAsync(test.NewContext(), pc, pushed.Latest, 10);
        var dto = Assert.Single(changes.Clips);
        Assert.True(dto.HasImage);
        Assert.Equal(640, dto.ImageWidth);

        // the search finds the text recognised in the picture
        var found = await ClipService.ListAsync(test.NewContext(), new Actor(anna.Id, false), new ClipQuery("konzept", null, null, "copied", true, 1, 50));
        Assert.Single(found.Items);
    }

    [DbFact]
    public async Task AnotherAccountSeesNothing()
    {
        await using var test = await TestDatabase.CreateAsync();
        var anna = await test.AddUserAsync("anna");
        var ben = await test.AddUserAsync("ben", admin: true);
        var png = Samples.Picture(10, 10);
        var clip = Samples.Image(png);
        await SyncService.PushAsync(test.Db, Device(anna), new PushRequest([clip]));
        await SyncService.UploadImageAsync(test.NewContext(), Device(anna), clip.SyncId, png);

        var benPc = Device(ben);
        Assert.Empty((await SyncService.ChangesAsync(test.NewContext(), benPc, 0, 100)).Clips);
        Assert.Null(await SyncService.ImageAsync(test.NewContext(), benPc, clip.SyncId, false));
        Assert.False((await SyncService.UploadImageAsync(test.NewContext(), benPc, clip.SyncId, png)).Ok);
        var annaClipId = await test.NewContext().Clips.Select(c => c.Id).SingleAsync();
        Assert.True((await ClipService.GetAsync(test.NewContext(), new Actor(ben.Id, true), annaClipId)).NotFound, "an administrator has no access to the clips of others");
    }

    [DbFact]
    public async Task ClearingTheHistoryKeepsFavouritesAndPinned()
    {
        await using var test = await TestDatabase.CreateAsync();
        var anna = await test.AddUserAsync("anna");
        var pc = Device(anna);
        await SyncService.PushAsync(test.Db, pc, new PushRequest([
            Samples.Text("one"), Samples.Text("two", favorite: true), Samples.Text("three") with { IsPinned = true }, Samples.Text("four")]));

        var removed = await ClipService.ClearHistoryAsync(test.NewContext(), new Actor(anna.Id, false));
        Assert.Equal(2, removed);
        var left = await ClipService.ListAsync(test.NewContext(), new Actor(anna.Id, false), new ClipQuery(null, null, null, "copied", true, 1, 50));
        Assert.Equal(2, left.Items.Count);
        var tombstones = (await SyncService.ChangesAsync(test.NewContext(), pc, 0, 100)).Clips.Count(c => c.IsDeleted);
        Assert.Equal(2, tombstones);
    }

    [DbFact]
    public async Task RetentionRemovesOldClipsButNotMarkedOnes()
    {
        await using var test = await TestDatabase.CreateAsync();
        var anna = await test.AddUserAsync("anna");
        var pc = Device(anna);
        var old = DateTimeOffset.UtcNow.AddDays(-40);
        await SyncService.PushAsync(test.Db, pc, new PushRequest([
            Samples.Text("old", old), Samples.Text("old favourite", old, favorite: true), Samples.Text("new")]));

        Assert.Equal(1, await ClipService.ApplyRetentionForAllAsync(test.NewContext()));
        var left = await ClipService.ListAsync(test.NewContext(), new Actor(anna.Id, false), new ClipQuery(null, null, null, "copied", true, 1, 50));
        Assert.Equal(["new", "old favourite"], left.Items.Select(i => i.Preview));

        // keeping forever: nothing more goes
        Assert.True((await AccountService.SaveGeneralAsync(test.NewContext(), new Actor(anna.Id, false), "Anna", 0)).Ok);
        Assert.Equal(0, await ClipService.ApplyRetentionForAllAsync(test.NewContext()));
    }

    [DbFact]
    public async Task TheLongPollWakesUpOnAChangeOfTheAccount()
    {
        await using var test = await TestDatabase.CreateAsync();
        var anna = await test.AddUserAsync("anna");
        var pc = Device(anna);
        var latest = await Revisions.LatestAsync(test.Db, anna.Id);

        var waiting = ClipService.WaitAsync(test.NewContext(), pc, latest, TimeSpan.FromSeconds(20), CancellationToken.None);
        await Task.Delay(300);
        Assert.False(waiting.IsCompleted);

        await SyncService.PushAsync(test.NewContext(), Device(anna, "Mac"), new PushRequest([Samples.Text("from the Mac")]));
        var woke = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(woke > latest);

        // nothing new: the poll ends after its time with the same revision
        var idle = await ClipService.WaitAsync(test.NewContext(), pc, woke, TimeSpan.FromMilliseconds(500), CancellationToken.None);
        Assert.Equal(woke, idle);
    }

    [DbFact]
    public async Task DevicesSignInAndARemovedDeviceIsRejected()
    {
        await using var test = await TestDatabase.CreateAsync();
        var anna = await test.AddUserAsync("anna", password: "anna-password");

        var wrong = await DeviceService.LoginAsync(test.Db, new DeviceLoginRequest("anna", "nope", "PC-1", "windows", "1.0"), null, "test");
        Assert.False(wrong.Ok);

        var login = await DeviceService.LoginAsync(test.NewContext(), new DeviceLoginRequest("Anna", "anna-password", "PC-1", "windows", "1.0"), null, "test");
        Assert.True(login.Ok, login.Error);
        var device = await DeviceService.AuthenticateAsync(test.NewContext(), login.Value!.Token);
        Assert.NotNull(device);
        Assert.Equal(anna.Id, device.UserId);

        Assert.True((await DeviceService.RemoveAsync(test.NewContext(), new Actor(anna.Id, false), login.Value.DeviceId)).Ok);
        Assert.Null(await DeviceService.AuthenticateAsync(test.NewContext(), login.Value.Token));

        // signing in again under the same name replaces the old entry
        await DeviceService.LoginAsync(test.NewContext(), new DeviceLoginRequest("anna", "anna-password", "PC-2", "macos", "1.0"), null, "test");
        await DeviceService.LoginAsync(test.NewContext(), new DeviceLoginRequest("anna", "anna-password", "PC-2", "macos", "1.1"), null, "test");
        var (devices, _) = await DeviceService.ListAsync(test.NewContext(), new Actor(anna.Id, false), null, "seen", true);
        Assert.Equal("1.1", Assert.Single(devices).AppVersion);
    }
}
