using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using ThixClip.Shared;

namespace ThixClip.Cloud.Tests;

/// <summary>The API of the devices over HTTP, the whole app in memory against a real PostgreSQL (THIXCLIP_TEST_DB).</summary>
public sealed class ApiTests
{
    private static WebApplicationFactory<Program> App(TestDatabase test, string dataDirectory) => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
    {
        b.UseEnvironment("Testing");
        b.UseSetting("ConnectionStrings:Default", test.ConnectionString);
        b.UseSetting("DataDirectory", dataDirectory);
    });

    private static async Task<HttpClient> SignedInAsync(WebApplicationFactory<Program> app, string device)
    {
        var client = app.CreateClient();
        var login = await client.PostAsJsonAsync(SyncContract.DeviceLogin, new DeviceLoginRequest("anna", "anna-password", device, "windows", "0.1.1"), SyncContract.Json);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal("1", login.Headers.GetValues(SyncContract.ProtocolHeader).Single());
        var answer = (await login.Content.ReadFromJsonAsync<DeviceLoginResponse>(SyncContract.Json))!;
        Assert.Equal("anna", answer.Account.UserName);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", answer.Token);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ThixClip/0.1.2");
        return client;
    }

    [DbFact]
    public async Task ADeviceSyncsTextsAndImagesAndLosesAccessWhenItSignsOut()
    {
        await using var test = await TestDatabase.CreateAsync();
        await test.AddUserAsync("anna", password: "anna-password");
        var data = Directory.CreateTempSubdirectory("thixclip-test-").FullName;
        await using var app = App(test, data);

        var anonymous = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(SyncContract.Changes)).StatusCode);
        var wrong = await anonymous.PostAsJsonAsync(SyncContract.DeviceLogin, new DeviceLoginRequest("anna", "wrong", "PC", "windows", "1"), SyncContract.Json);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.False(string.IsNullOrEmpty((await wrong.Content.ReadFromJsonAsync<ApiError>(SyncContract.Json))!.Message));

        var pc = await SignedInAsync(app, "PC-1");
        var mac = await SignedInAsync(app, "Mac");

        // the Mac waits; the PC pushes a text and a picture
        var waiting = mac.GetFromJsonAsync<WaitResult>($"{SyncContract.Wait}?since=0&timeout=20", SyncContract.Json);
        var png = Samples.Picture(300, 200);
        var text = Samples.Text("Release-Plan Q2: Features, Tests, Rollout");
        var image = Samples.Image(png, ocr: "Diagramm");
        var push = await pc.PostAsJsonAsync(SyncContract.Push, new PushRequest([text, image]), SyncContract.Json);
        Assert.Equal(HttpStatusCode.OK, push.StatusCode);
        var pushed = (await push.Content.ReadFromJsonAsync<PushResponse>(SyncContract.Json))!;
        Assert.True(pushed.Results[1].NeedsImage);
        Assert.True((await waiting.WaitAsync(TimeSpan.FromSeconds(10)))!.Latest > 0);

        var upload = await pc.PutAsync(SyncContract.Image(image.SyncId), new ByteArrayContent(png) { Headers = { ContentType = new MediaTypeHeaderValue("image/png") } });
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);

        var changes = (await mac.GetFromJsonAsync<ChangeSet>($"{SyncContract.Changes}?since=0", SyncContract.Json))!;
        Assert.Equal(2, changes.Clips.Count);
        Assert.Equal("PC-1", changes.Clips[0].Source);
        Assert.True(changes.Clips.Single(c => c.Kind == ClipKind.Image).HasImage);
        Assert.Equal(png, await mac.GetByteArrayAsync(SyncContract.Image(image.SyncId)));
        Assert.Equal("image/png", (await mac.GetAsync(SyncContract.Thumbnail(image.SyncId))).Content.Headers.ContentType!.MediaType);

        // a bad push is refused whole, with the field
        var bad = await pc.PostAsJsonAsync(SyncContract.Push, new PushRequest([Samples.Text("x") with { ContentHash = "nope" }]), SyncContract.Json);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("clips[0].contentHash", (await bad.Content.ReadFromJsonAsync<ApiError>(SyncContract.Json))!.Field);

        // the account: retention from a device
        var account = await mac.PutAsJsonAsync(SyncContract.Account, new AccountSettingsUpdate(7), SyncContract.Json);
        Assert.Equal(7, (await account.Content.ReadFromJsonAsync<AccountInfo>(SyncContract.Json))!.RetentionDays);
        Assert.Equal(HttpStatusCode.BadRequest, (await mac.PutAsJsonAsync(SyncContract.Account, new AccountSettingsUpdate(3), SyncContract.Json)).StatusCode);

        // signing out ends the token
        Assert.Equal(HttpStatusCode.NoContent, (await pc.PostAsync(SyncContract.DeviceLogout, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await pc.GetAsync(SyncContract.Changes)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await mac.GetAsync(SyncContract.Changes)).StatusCode);
    }

    [DbFact]
    public async Task PagesNeedASignInAndTheSetupComesFirst()
    {
        await using var test = await TestDatabase.CreateAsync();
        var data = Directory.CreateTempSubdirectory("thixclip-test-").FullName;
        await using var app = App(test, data);
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // a fresh installation leads to the setup assistant
        var first = await client.GetAsync("/Clips");
        Assert.Equal("/Account/Setup", first.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Account/Setup")).StatusCode);

        await test.AddUserAsync("anna");
        var fresh = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        // the setup state is remembered per process: a new factory would see the account; here the redirect to the sign-in is what matters
        foreach (var path in new[] { "/manifest.webmanifest", "/sw.js", "/robots.txt", "/llms.txt", "/offline.html", "/health" })
        {
            Assert.Equal(HttpStatusCode.OK, (await fresh.GetAsync(path)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await fresh.GetAsync("/live/revision?since=0&timeout=1")).StatusCode);
        var page = await fresh.GetAsync("/Account/Login");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("script-src 'self'", page.Headers.GetValues("Content-Security-Policy").Single());
    }
}
