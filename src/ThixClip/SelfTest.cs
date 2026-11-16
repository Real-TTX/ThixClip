using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using SkiaSharp;
using ThixClip.Core.Business;
using ThixClip.Core.Infrastructure;
using ThixClip.Core.Models;
using ThixClip.Core.Platform;
using ThixClip.Shared;

namespace ThixClip;

/// <summary>
/// "ThixClip.exe --self-test &lt;report file&gt;": the published program checks itself – everything trimming or
/// single-file packaging could break silently: JSON of settings, sync and update manifest, SQLite, SkiaSharp, the
/// protected token store, the OCR engine of the system, Windows shortcuts (COM) and the windows of the app with their
/// bindings. It runs in a fresh temporary data folder (Program sets it), never touches the user's data, writes a report
/// and ends with exit code 0 (all passed) or 1. CI runs it on every Windows build.
/// </summary>
internal static class SelfTest
{
    public const string Argument = "--self-test";

    public static async Task<int> RunAsync(string reportPath)
    {
        var report = new StringBuilder($"ThixClip {AppInfo.Version} self test, data folder {AppPaths.DataDirectory}{Environment.NewLine}");
        var failures = 0;

        async Task Check(string name, Func<Task<string>> check)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                var note = await check();
                report.AppendLine($"ok    {name} ({watch.ElapsedMilliseconds} ms){(note.Length > 0 ? " – " + note : "")}");
            }
            catch (Exception exception)
            {
                failures++;
                report.AppendLine($"FAIL  {name}: {exception}");
            }
        }

        await Check("settings (JSON)", () =>
        {
            SettingsService.Update(settings => settings with { RetentionDays = 7, Theme = ThemeChoice.Dark });
            SettingsStore.Reset();
            var read = SettingsService.Get();
            Ensure(read is { RetentionDays: 7, Theme: ThemeChoice.Dark }, "settings did not survive a save");
            return Task.FromResult("");
        });

        await Check("update manifest (JSON)", () =>
        {
            const string json = """{"version":"0.1.9-20261115","channel":"release","buildNumber":9,"setup":"ThixClipSetup.exe","sha256":"AB","packages":{"win-x64":{"file":"ThixClipSetup.exe","sha256":"AB"}}}""";
            var manifest = new HttpResponseMessage { Content = new StringContent(json, Encoding.UTF8, "application/json") }.Content
                .ReadFromJsonAsync<UpdateManifest>().GetAwaiter().GetResult();
            Ensure(manifest is { BuildNumber: 9 } && UpdateService.PackageFor(manifest, "win-x64")?.File == "ThixClipSetup.exe", "manifest not read");
            return Task.FromResult("");
        });

        await Check("sync contract (JSON)", () =>
        {
            var now = DateTimeOffset.UtcNow;
            var clip = new ClipDto { SyncId = Guid.NewGuid(), Kind = ClipKind.Text, Text = "x", ContentHash = "AB", Revision = 3, CreatedAt = now, CopiedAt = now, UpdatedAt = now };
            var json = JsonSerializer.Serialize(new ChangeSet([clip], 3, false, 30), SyncContract.Json);
            var back = JsonSerializer.Deserialize<ChangeSet>(json, SyncContract.Json);
            Ensure(back is { Latest: 3 } && back.Clips[0].SyncId == clip.SyncId, "change set not read back");
            return Task.FromResult("");
        });

        await Check("history (SQLite, SkiaSharp)", () =>
        {
            ClipService.Initialize();
            Ensure(ClipService.CaptureText("Selbsttest") is not null, "text not recorded");
            var image = ImageCodec.Normalize(Render("ThixClip 2026", 640, 160)) ?? throw new InvalidOperationException("image not normalised");
            var entry = ClipService.CaptureImage(image) ?? throw new InvalidOperationException("image not recorded");
            Ensure(ClipService.ReadImage(entry.Id)?.Length > 0 && ClipService.ReadThumbnail(entry.Id)?.Length > 0, "image not stored");
            Ensure(ClipService.Query(new ClipQuery { Search = "selbsttest" }).Count == 1, "search failed");
            return Task.FromResult($"{ClipService.Count} entries");
        });

        await Check("token store", () =>
        {
            var stored = TokenStore.Save("self-test", "geheim");
            Ensure(TokenStore.Read("self-test", stored) == "geheim", "token not read back");
            TokenStore.Delete("self-test");
            return Task.FromResult("");
        });

        await Check("OCR", async () =>
        {
            if (!TextRecognition.IsAvailable)
            {
                return "no engine on this system (skipped)";
            }

            var text = await TextRecognition.RecognizeAsync(Render("ThixClip 2026", 900, 220), CancellationToken.None);
            Ensure(text?.Contains("ThixClip", StringComparison.OrdinalIgnoreCase) == true, $"recognised \"{text}\"");
            return $"\"{text}\"";
        });

        if (OperatingSystem.IsWindows())
        {
            await Check("shortcut (COM)", () =>
            {
                var link = Path.Combine(AppPaths.DataDirectory, "ThixClip.lnk");
                Core.Platform.Windows.ShellLink.Write(link, App.ExecutablePath, AppPaths.DataDirectory, "Selbsttest", App.ExecutablePath);
                Ensure(File.Exists(link) && new FileInfo(link).Length > 0, "no .lnk written");
                return Task.FromResult("");
            });
        }

        await Check("windows and bindings", async () =>
        {
            var window = new MainWindow();
            window.ShowFromTray(rememberForeground: false);
            await Settle();
            var history = (Pages.HistoryPage)window.PageHost.Content!;
            Ensure(history.Selected is { } selected && history.ClipList.ContainerFromItem(selected) is not null, "no row shown");
            Ensure(window.PauseLabel.Text == "Pausieren", "pause button not bound");
            window.ShowSettings();
            await Settle();
            window.ShowAbout();
            await Settle();

            var pause = Dialogs.PauseDialog.AskAsync(window, PauseLength.Minutes30);
            await Settle();
            foreach (var owned in window.OwnedWindows.ToList())
            {
                owned.Close();
            }

            Ensure(await pause is null, "pause dialog answered without a choice");
            if (OperatingSystem.IsWindows())
            {
                var setup = Setup.ThixClipSetup.Create(isUninstall: false, InstallService.IsInstalled, InstallService.InstalledVersion, InstallService.ProgramFolder, _ => Task.CompletedTask);
                setup.Show();
                await Settle();
                setup.Close();
            }

            ProblemReports.WriteHang(TimeSpan.FromSeconds(9), withDump: false);
            Dialogs.ProblemReportDialog.ShowUnseen()?.Close();
            window.IsExiting = true;
            window.Close();
            return "";
        });

        report.AppendLine(failures == 0 ? "PASSED" : $"FAILED ({failures})");
        await File.WriteAllTextAsync(reportPath, report.ToString(), Encoding.UTF8);

        // The temporary data folder goes again (unless the report was written into it).
        HistoryStore.Close();
        if (!Path.GetFullPath(reportPath).StartsWith(Path.GetFullPath(AppPaths.DataDirectory), StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                Directory.Delete(AppPaths.DataDirectory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        return failures == 0 ? 0 : 1;
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static async Task Settle()
    {
        for (var round = 0; round < 10; round++)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            await Task.Delay(30);
        }
    }

    private static byte[] Render(string text, int width, int height)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        surface.Canvas.Clear(SKColors.White);
        using var paint = new SKPaint { Color = SKColors.Black, TextSize = height * 0.45f, IsAntialias = true, Typeface = SKTypeface.FromFamilyName("Arial") };
        surface.Canvas.DrawText(text, width * 0.05f, height * 0.65f, paint);
        using var image = surface.Snapshot();
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }
}
