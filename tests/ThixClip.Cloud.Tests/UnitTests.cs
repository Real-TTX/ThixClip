using System.Text.Json;
using SkiaSharp;
using ThixClip.Cloud.Business;
using ThixClip.Cloud.Web;
using ThixClip.Shared;

namespace ThixClip.Cloud.Tests;

/// <summary>What needs no database: content, images, validation, the contract's JSON, the list tools.</summary>
public sealed class UnitTests
{
    [Fact]
    public void HashOfTextIsSha256OfUtf8InUpperCase()
    {
        // SHA-256("abc") is well known
        Assert.Equal("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", ClipContent.HashText("abc"));
        Assert.NotEqual(ClipContent.HashText("abc"), ClipContent.HashText("abc "));
        Assert.Equal(ClipContent.HashText("Grüße"), ClipContent.HashBytes(System.Text.Encoding.UTF8.GetBytes("Grüße")));
    }

    [Theory]
    [InlineData("https://www.example.com/produkt", true)]
    [InlineData("  http://example.com  ", true)]
    [InlineData("https://example.com/a b", false)]
    [InlineData("ftp://example.com", false)]
    [InlineData("Produktmeeting morgen um 10 Uhr", false)]
    [InlineData("see https://example.com", false)]
    public void ALinkIsExactlyOneHttpAddress(string text, bool isLink)
    {
        Assert.Equal(isLink, ClipContent.IsLink(text));
        Assert.Equal(isLink ? ClipKind.Link : ClipKind.Text, ClipContent.KindOfText(text));
    }

    [Fact]
    public void PreviewIsOneLineAndShort()
    {
        Assert.Equal("To-do: Konzept finalisieren, Feedback", ClipContent.Preview("To-do:\r\nKonzept finalisieren,\n\tFeedback"));
        var preview = ClipContent.Preview(new string('x', 500), 100);
        Assert.Equal(101, preview.Length);
        Assert.EndsWith("…", preview);
    }

    [Fact]
    public void NormalizeHashAcceptsOnlySha256()
    {
        var hash = ClipContent.HashText("x");
        Assert.Equal(hash, ClipContent.NormalizeHash(hash.ToLowerInvariant()));
        Assert.Null(ClipContent.NormalizeHash("abc"));
        Assert.Null(ClipContent.NormalizeHash(null));
    }

    [Fact]
    public void APngIsKeptAsItCameWithAThumbnail()
    {
        var png = Samples.Picture(1920, 1080);
        var outcome = ImageTools.Prepare(png);
        Assert.True(outcome.Ok, outcome.Error);
        var image = outcome.Value!;
        Assert.Equal(png, image.Png);
        Assert.Equal(1920, image.Width);
        Assert.Equal(1080, image.Height);
        Assert.Equal(ClipContent.HashBytes(png), image.ContentHash);

        using var thumbnail = SKBitmap.Decode(image.Thumbnail);
        Assert.Equal(SyncContract.ThumbnailSize, thumbnail.Width);
        Assert.Equal(180, thumbnail.Height);
    }

    [Fact]
    public void AJpegBecomesAPng()
    {
        var jpeg = Samples.Picture(200, 100, SKEncodedImageFormat.Jpeg);
        var outcome = ImageTools.Prepare(jpeg);
        Assert.True(outcome.Ok, outcome.Error);
        using var codec = SKCodec.Create(new SKMemoryStream(outcome.Value!.Png));
        Assert.Equal(SKEncodedImageFormat.Png, codec.EncodedFormat);
        Assert.Equal(200, outcome.Value.Width);
    }

    [Fact]
    public void SomethingThatIsNoImageIsRefused()
    {
        Assert.False(ImageTools.Prepare("not an image"u8.ToArray()).Ok);
        Assert.False(ImageTools.Prepare([]).Ok);
    }

    [Fact]
    public void PushValidationNamesTheField()
    {
        Assert.Null(SyncService.Validate(new PushRequest([Samples.Text("hello")])));

        var wrongHash = Samples.Text("hello") with { ContentHash = ClipContent.HashText("other") };
        Assert.Equal("clips[0].contentHash", SyncService.Validate(new PushRequest([wrongHash]))!.Value.Field);

        var empty = Samples.Text("hello") with { Text = "  " };
        Assert.Equal("clips[0].text", SyncService.Validate(new PushRequest([empty]))!.Value.Field);

        var noId = Samples.Text("hello") with { SyncId = Guid.Empty };
        Assert.Equal("clips[0].syncId", SyncService.Validate(new PushRequest([noId]))!.Value.Field);

        // a tombstone needs no content
        var tombstone = Samples.Text("hello") with { IsDeleted = true, Text = null, ContentHash = string.Empty };
        Assert.Null(SyncService.Validate(new PushRequest([tombstone])));

        var tooMany = Enumerable.Range(0, SyncContract.MaxBatchSize + 1).Select(i => Samples.Text("t" + i)).ToList();
        Assert.Equal("clips", SyncService.Validate(new PushRequest(tooMany))!.Value.Field);
    }

    [Fact]
    public void TheContractSpeaksCamelCaseWithEnumsAsText()
    {
        var dto = Samples.Text("https://example.com");
        var json = JsonSerializer.Serialize(dto, SyncContract.Json);
        Assert.Contains("\"syncId\"", json);
        Assert.Contains("\"kind\":\"Link\"", json);
        Assert.DoesNotContain("\"ocrText\"", json); // nulls are left out

        var back = JsonSerializer.Deserialize<ClipDto>(json, SyncContract.Json)!;
        Assert.Equal(dto, back);
    }

    [Theory]
    [InlineData("anna", true)]
    [InlineData("anna.meier@example.com", true)]
    [InlineData("a", false)]
    [InlineData("Anna", false)] // the business layer lowers it first
    [InlineData("-anna", false)]
    [InlineData("an na", false)]
    public void UserNames(string name, bool ok) => Assert.Equal(ok, UserService.UserNameProblem(name) is null);

    [Fact]
    public void PasswordRules()
    {
        Assert.NotNull(Passwords.Problem("short"));
        Assert.NotNull(Passwords.Problem("long-enough", "different"));
        Assert.Null(Passwords.Problem("long-enough", "long-enough"));
    }

    [Fact]
    public void ToolbarKeepsEveryToolButTheOneRemoved()
    {
        var toolbar = new ToolbarModel
        {
            Id = "clips",
            Action = "/Clips",
            Search = "meeting",
            Filters = [new FilterField("kind", "Typ", [new("image", "Bild")], "image")],
            SortOptions = [new SortOption("copied", "Kopiert")],
            Sort = "copied",
            Descending = false,
            DefaultSort = "copied",
            DefaultDescending = true,
        };
        Assert.Equal("/Clips?q=meeting&kind=image&sort=copied&dir=asc", toolbar.UrlWithout(string.Empty));
        Assert.Equal("/Clips?kind=image&sort=copied&dir=asc", toolbar.UrlWithout("q"));
        Assert.Equal("/Clips?q=meeting&kind=image&sort=copied&dir=asc&page=3", toolbar.UrlWithPage(3));
        Assert.Equal("/Clips", toolbar.UrlReset());
    }

    [Fact]
    public void RetentionChoicesHaveNames()
    {
        foreach (var days in SyncContract.RetentionChoices)
        {
            Assert.False(string.IsNullOrEmpty(AccountService.RetentionName(days)));
        }

        Assert.Equal("Unbegrenzt", AccountService.RetentionName(0));
    }

    [Fact]
    public void NumbersAndSizesAreGerman()
    {
        Assert.Equal("12.345", Ui.Number(12345));
        Assert.Equal("1,5 MB", Ui.Bytes(1536 * 1024));
    }
}
