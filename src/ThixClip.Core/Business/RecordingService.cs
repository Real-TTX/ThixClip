using ThixClip.Core.Models;

namespace ThixClip.Core.Business;

/// <summary>How long a pause of the recording lasts.</summary>
public enum PauseLength
{
    Minutes15 = 0,
    Minutes30 = 1,
    Hour1 = 2,
    UntilRestart = 3,
    UntilResumed = 4,
}

/// <summary>
/// Recording on or paused. A pause lasts a while (15 min, 30 min, 1 hour: the end is stored, so it also survives a
/// restart), until ThixClip starts again (kept in memory only) or until the user resumes (stored). A timed pause ends by
/// itself; <see cref="Changed"/> tells the app (tray icon, button) every time the state changes. No configuration lock:
/// pausing is an everyday switch.
/// </summary>
public static class RecordingService
{
    public static readonly IReadOnlyList<PauseLength> Choices =
        [PauseLength.Minutes15, PauseLength.Minutes30, PauseLength.Hour1, PauseLength.UntilRestart, PauseLength.UntilResumed];

    private static readonly object Gate = new();
    private static bool _pausedUntilRestart;
    private static Timer? _endTimer;

    /// <summary>Raised when recording is paused or runs again (any thread).</summary>
    public static event EventHandler? Changed;

    public static bool IsPaused => _pausedUntilRestart || IsStoredPauseActive(SettingsService.Get(), DateTimeOffset.UtcNow);

    /// <summary>The end of a timed pause (null: no pause, or one without an end).</summary>
    public static DateTimeOffset? PausedUntil
    {
        get
        {
            var settings = SettingsService.Get();
            return !_pausedUntilRestart && IsStoredPauseActive(settings, DateTimeOffset.UtcNow) ? settings.RecordingPausedUntil : null;
        }
    }

    /// <summary>"Pausiert bis 14:30", "Pausiert bis zum Neustart", "Pausiert" – or "" while recording.</summary>
    public static string Describe()
    {
        if (_pausedUntilRestart)
        {
            return "Pausiert bis zum Neustart";
        }

        if (!IsPaused)
        {
            return "";
        }

        return PausedUntil is { } until ? $"Pausiert bis {until.ToLocalTime():HH:mm}" : "Pausiert";
    }

    public static string Text(PauseLength length) => length switch
    {
        PauseLength.Minutes15 => "15 Minuten",
        PauseLength.Minutes30 => "30 Minuten",
        PauseLength.Hour1 => "1 Stunde",
        PauseLength.UntilRestart => "Bis ThixClip neu startet",
        PauseLength.UntilResumed => "Bis ich fortsetze",
        _ => length.ToString(),
    };

    /// <summary>At start: a timed pause that has ended is cleared, one still running gets its timer again.</summary>
    public static void Initialize()
    {
        var settings = SettingsService.Get();
        if (settings.IsRecordingPaused && !IsStoredPauseActive(settings, DateTimeOffset.UtcNow))
        {
            Store(paused: false, until: null);
            return;
        }

        ScheduleEnd(settings.IsRecordingPaused ? settings.RecordingPausedUntil : null);
    }

    public static void Pause(PauseLength length)
    {
        if (!Enum.IsDefined(length))
        {
            throw new BusinessException("Diese Pausendauer gibt es nicht.");
        }

        var minutes = length switch
        {
            PauseLength.Minutes15 => 15,
            PauseLength.Minutes30 => 30,
            PauseLength.Hour1 => 60,
            _ => 0,
        };

        lock (Gate)
        {
            _pausedUntilRestart = length == PauseLength.UntilRestart;
            if (_pausedUntilRestart)
            {
                Store(paused: false, until: null);
                ScheduleEnd(null);
            }
            else
            {
                var until = minutes > 0 ? DateTimeOffset.UtcNow.AddMinutes(minutes) : (DateTimeOffset?)null;
                Store(paused: true, until: until);
                ScheduleEnd(until);
            }
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void Resume()
    {
        lock (Gate)
        {
            _pausedUntilRestart = false;
            Store(paused: false, until: null);
            ScheduleEnd(null);
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    internal static void ResetForTests()
    {
        _pausedUntilRestart = false;
        ScheduleEnd(null);
    }

    private static bool IsStoredPauseActive(AppSettings settings, DateTimeOffset now) =>
        settings.IsRecordingPaused && (settings.RecordingPausedUntil is not { } until || until > now);

    private static void Store(bool paused, DateTimeOffset? until) => SettingsService.SetRecordingPaused(paused, until);

    private static void ScheduleEnd(DateTimeOffset? until)
    {
        _endTimer?.Dispose();
        _endTimer = null;
        if (until is not { } end)
        {
            return;
        }

        var due = end - DateTimeOffset.UtcNow;
        _endTimer = new Timer(_ => Resume(), null, due < TimeSpan.Zero ? TimeSpan.Zero : due, Timeout.InfiniteTimeSpan);
    }
}
