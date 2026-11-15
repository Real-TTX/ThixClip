using SkiaSharp;
using ThixClip.Core.Business;
using ThixClip.Core.Infrastructure;
using ThixClip.Core.Models;

namespace ThixClip.Tests;

public sealed class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Win+V", true, false, false, true, "V")]
    [InlineData("ctrl + alt + v", true, true, false, false, "V")]
    [InlineData("Strg+Umschalt+F5", true, false, true, false, "F5")]
    [InlineData("Cmd+Shift+Space", false, false, true, true, "Space")]
    [InlineData("⌃⌥V", false, false, false, false, null)]
    [InlineData("Ctrl+Win", false, false, false, false, null)]
    [InlineData("Ctrl+V+B", false, false, false, false, null)]
    public void Parse(string text, bool control, bool alt, bool shift, bool meta, string? key)
    {
        var hotkey = HotkeyService.Parse(text);
        if (key is null)
        {
            Assert.Null(hotkey);
            return;
        }

        Assert.Equal(new Hotkey(control, alt, shift, meta, key), hotkey);
    }

    [Fact]
    public void Format_and_parse_round_trip()
    {
        var hotkey = new Hotkey(true, true, true, true, "K");
        Assert.Equal(hotkey, HotkeyService.Parse(HotkeyService.Format(hotkey)));
    }

    [Fact]
    public void The_default_is_ctrl_shift_v_on_windows_and_ctrl_option_v_on_macos()
    {
        var expected = OperatingSystem.IsMacOS() ? "Ctrl+Alt+V" : "Ctrl+Shift+V";
        Assert.Equal(expected, HotkeyService.DefaultHotkey);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("Strg + Umschalt + V", HotkeyService.Display(HotkeyService.DefaultHotkey));
        }
        else if (OperatingSystem.IsMacOS())
        {
            Assert.Equal("⌃⌥V", HotkeyService.Display(HotkeyService.DefaultHotkey));
        }
    }

    [Fact]
    public void A_global_shortcut_needs_a_real_modifier()
    {
        Assert.Throws<BusinessException>(() => HotkeyService.Validate(new Hotkey(false, false, true, false, "V")));
        Assert.Throws<BusinessException>(() => HotkeyService.Validate(new Hotkey(true, false, false, false, "V")));
        Assert.Throws<BusinessException>(() => HotkeyService.Validate(new Hotkey(false, true, false, false, "F")));
        HotkeyService.Validate(new Hotkey(false, false, true, true, "V"));
        HotkeyService.Validate(new Hotkey(false, false, false, false, "F9"));
        HotkeyService.Validate(new Hotkey(true, false, false, true, "V"));
        HotkeyService.Validate(new Hotkey(true, true, false, false, "V"));
        if (OperatingSystem.IsWindows())
        {
            HotkeyService.Validate(new Hotkey(false, false, false, true, "V")); // replaces Win+V
        }
    }

    [Fact]
    public void Key_codes_of_both_systems()
    {
        Assert.Equal(0x56, HotkeyService.WindowsKeyCode("V"));
        Assert.Equal(0x31, HotkeyService.WindowsKeyCode("1"));
        Assert.Equal(0x74, HotkeyService.WindowsKeyCode("F5"));
        Assert.Equal(0x09u, HotkeyService.MacKeyCode("V"));
        Assert.Equal(0x31u, HotkeyService.MacKeyCode("Space"));
        foreach (var key in Enumerable.Range('A', 26).Select(code => ((char)code).ToString()).Concat(Enumerable.Range(0, 10).Select(digit => digit.ToString())))
        {
            HotkeyService.MacKeyCode(key);
            HotkeyService.WindowsKeyCode(key);
        }
    }
}

public sealed class ImageCodecTests
{
    [Fact]
    public void A_windows_dib_with_zero_alpha_becomes_an_opaque_png()
    {
        var image = ImageCodec.FromDib(TestImages.Dib32(64, 32, blue: 200, green: 100, red: 50))!;
        Assert.Equal((64, 32), (image.Width, image.Height));
        using var bitmap = SKBitmap.Decode(image.Png);
        var pixel = bitmap.GetPixel(10, 10);
        Assert.Equal(255, pixel.Alpha);
        Assert.Equal((50, 100, 200), (pixel.Red, pixel.Green, pixel.Blue));
    }

    [Fact]
    public void Png_to_dib_and_back_keeps_size_and_colour()
    {
        var png = TestImages.Png(30, 20, new SKColor(10, 120, 230));
        var back = ImageCodec.FromDib(ImageCodec.ToDib(png))!;
        using var bitmap = SKBitmap.Decode(back.Png);
        Assert.Equal((30, 20), (bitmap.Width, bitmap.Height));
        Assert.Equal(new SKColor(10, 120, 230), bitmap.GetPixel(1, 1));
    }

    [Fact]
    public void The_same_picture_gives_the_same_png_bytes()
    {
        var first = ImageCodec.Normalize(TestImages.Png(50, 50, SKColors.Teal))!;
        var second = ImageCodec.FromDib(ImageCodec.ToDib(first.Png))!;
        Assert.Equal(ClipService.HashOf(first.Png), ClipService.HashOf(second.Png));
    }

    [Fact]
    public void Not_an_image_is_null() => Assert.Null(ImageCodec.Normalize("kein Bild"u8.ToArray()));
}
