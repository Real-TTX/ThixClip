using ThixClip.Core.Business;
using ThixClip.Core.Infrastructure;
using ThixClip.Core.Platform;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ThixClip.Tests;

/// <summary>
/// Every test gets its own data folder, an empty history, a clipboard in memory, a token store in memory and no machine
/// policies. Nothing a test does touches the real history, settings, clipboard, Keychain or registry, and no address or folder
/// opens (<see cref="Ui.OpenExternalForTests"/>).
/// </summary>
internal sealed class TestData : IDisposable
{
    public TestData()
    {
        Folder = Path.Combine(Path.GetTempPath(), "thixclip-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Folder);
        HistoryStore.Close();
        AppPaths.UseDirectoryForTests(Folder);
        SettingsStore.Reset();
        ClipService.ResetForTests();
        ClipboardMonitor.ResetForTests();
        RecordingService.ResetForTests();
        TokenStore.UseMemoryForTests(true);
        AccessPolicy.UsePolicyReaderForTests(_ => false);
        Clipboard = new FakeClipboard();
        NativeClipboard.UseForTests(Clipboard);
        CloudClient.HandlerForTests = null;
        Ui.OpenExternalForTests = _ => { }; // never a real browser or folder
        SyncService.ResetForTests();
        ClipService.Initialize();
    }

    public string Folder { get; }

    public FakeClipboard Clipboard { get; }

    public void Dispose()
    {
        SyncService.Stop();
        HistoryStore.Close();
        CloudClient.HandlerForTests = null;
        Ui.OpenExternalForTests = null;
        NativeClipboard.UseForTests(null);
        AccessPolicy.UsePolicyReaderForTests(null);
        TokenStore.UseMemoryForTests(false);
        ClipService.ResetForTests();
        RecordingService.ResetForTests();
        SettingsStore.Reset();
        AppPaths.UseDirectoryForTests(null);
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>A clipboard in memory: every write is a change, like the system's.</summary>
internal sealed class FakeClipboard : IClipboardBackend
{
    private ClipboardSnapshot _content = ClipboardSnapshot.Empty;

    public long ChangeCount { get; private set; }

    public ClipboardSnapshot Read() => _content;

    public void Write(ClipboardSnapshot snapshot)
    {
        _content = snapshot;
        ChangeCount++;
    }

    public ClipboardSnapshot Content => _content;
}
