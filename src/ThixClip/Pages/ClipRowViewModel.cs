using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ThixClip.Core.Business;
using ThixClip.Core.Infrastructure;
using ThixClip.Core.Models;
using ThixClip.Shared;

namespace ThixClip.Pages;

/// <summary>A group heading of the list ("Angeheftet", "Heute", "Gestern" ...). Not selectable.</summary>
public sealed class GroupHeaderViewModel(string title, int count)
{
    public string Title { get; } = title;

    public string CountText { get; } = count == 1 ? "1 Eintrag" : $"{count} Einträge";

    /// <summary>The first heading has no gap above it.</summary>
    public bool IsFirst { get; init; }
}

/// <summary>
/// One entry of the history as the list and the detail pane show it. Reused while the entry exists (the list is
/// refreshed often); <see cref="Update"/> swaps the entry and announces what changed. Images load their thumbnail and
/// preview in the background on first use.
/// </summary>
public sealed class ClipRowViewModel(ClipEntry entry) : INotifyPropertyChanged
{
    private const int MaxDetailText = 100_000;

    private ClipEntry _entry = entry;
    private Bitmap? _thumbnail;
    private bool _thumbnailRequested;
    private Bitmap? _preview;
    private bool _previewRequested;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ClipEntry Entry => _entry;

    public long Id => _entry.Id;

    public bool IsImage => _entry.IsImage;

    public bool IsText => !_entry.IsImage;

    public bool IsLink => _entry.Kind == ClipKind.Link;

    public bool IsFavorite => _entry.IsFavorite;

    public bool IsPinned => _entry.IsPinned;

    public Geometry KindIcon => Ui.Icon(_entry.Kind switch
    {
        ClipKind.Link => "Icon.Link",
        ClipKind.Image => "Icon.Image",
        _ => "Icon.Text",
    });

    /// <summary>Two lines of the text (white space folded), or what an image shows.</summary>
    public string PreviewText
    {
        get
        {
            if (_entry.IsImage)
            {
                return _entry.OcrState == OcrState.Done && !string.IsNullOrWhiteSpace(_entry.OcrText) ? Fold(_entry.OcrText, 220) : ImageCaption;
            }

            return Fold(_entry.Text ?? "", 300);
        }
    }

    public bool HasOcrBadge => _entry.IsImage && _entry.OcrState == OcrState.Done && !string.IsNullOrWhiteSpace(_entry.OcrText);

    public string ImageCaption => _entry.ImageWidth is { } width && _entry.ImageHeight is { } height
        ? $"Bild (PNG, {width} × {height})"
        : "Bild (PNG)";

    public string TimeText => Ui.FormatTime(_entry.CopiedDate);

    /// <summary>The whole text for the detail pane (very long texts are cut for display; copy and paste use all of it).</summary>
    public string FullText
    {
        get
        {
            var text = _entry.Text ?? "";
            return text.Length <= MaxDetailText ? text : text[..MaxDetailText] + "\n\n… (gekürzt; Kopieren und Einfügen nehmen den ganzen Text)";
        }
    }

    public string OcrText => _entry.OcrText ?? "";

    public bool ShowOcrSection => _entry.IsImage;

    public bool HasOcrText => !string.IsNullOrWhiteSpace(_entry.OcrText);

    public string OcrPlaceholder => _entry.OcrState switch
    {
        OcrState.Pending => "Text wird erkannt …",
        OcrState.NoText => "Kein Text im Bild gefunden.",
        _ => "Texterkennung ist auf diesem Gerät nicht verfügbar oder ausgeschaltet.",
    };

    public string TypeText => _entry.Kind switch
    {
        ClipKind.Link => "Link",
        ClipKind.Image => "Bild (PNG)",
        _ => "Text",
    };

    public string OcrStatusText => _entry.OcrState switch
    {
        OcrState.Done => "Text erkannt",
        OcrState.NoText => "Kein Text",
        OcrState.Pending => "Wird erkannt …",
        _ => "Nicht verfügbar",
    };

    public bool OcrRecognized => _entry.OcrState == OcrState.Done;

    public string CreatedText => Ui.FormatDateTime(_entry.CreateDate);

    public string CopiedText => Ui.FormatDateTime(_entry.CopiedDate);

    public string SourceText => string.IsNullOrWhiteSpace(_entry.Source) ? "Dieser Computer" : _entry.Source;

    public string SizeText => _entry.IsImage
        ? $"{_entry.ImageWidth} × {_entry.ImageHeight} px{(_entry.ImageBytes is { } bytes ? $" · {Ui.FormatBytes(bytes)}" : "")}"
        : $"{(_entry.Text?.Length ?? 0):N0} Zeichen";

    public string RetentionText
    {
        get
        {
            if (_entry.IsFavorite)
            {
                return "Dauerhaft (Favorit)";
            }

            if (_entry.IsPinned)
            {
                return "Dauerhaft (angeheftet)";
            }

            var days = SettingsService.Get().RetentionDays;
            if (days == 0)
            {
                return "Unbegrenzt";
            }

            var left = (_entry.CopiedDate.AddDays(days) - AppClock.UtcNow).TotalDays;
            return left < 1 ? "Weniger als 1 Tag" : $"Noch {Math.Ceiling(left):0} Tage";
        }
    }

    public string FavoriteText => _entry.IsFavorite ? "Favorit entfernen" : "Favorit";

    public string PinText => _entry.IsPinned ? "Lösen" : "Anheften";

    public string PinTip => $"{PinText} ({Ui.CommandKey.Name}+P)";

    public string FavoriteTip => $"{FavoriteText} ({Ui.CommandKey.Name}+D)";

    /// <summary>What "Öffnen" does with this entry (Dialogs.ViewerDialog).</summary>
    public string OpenName => _entry.Kind switch
    {
        ClipKind.Image => "Bild ansehen",
        ClipKind.Link => "Link ansehen",
        _ => "Text ansehen",
    };

    public string OpenTip => $"{OpenName} ({Ui.CommandKey.Name}+O oder {Ui.CommandKey.Name}+Klick)";

    public bool IsImagePending => _entry.IsImage && !_entry.HasImage;

    /// <summary>Small picture for the list (decoded once, in the background).</summary>
    public Bitmap? Thumbnail
    {
        get
        {
            if (!_thumbnailRequested && _entry is { IsImage: true, HasImage: true })
            {
                _thumbnailRequested = true;
                var id = _entry.Id;
                _ = Task.Run(() => Decode(ClipService.ReadThumbnail(id), 280)).ContinueWith(task => Ui.Post(() =>
                {
                    _thumbnail = task.Result;
                    Raise(nameof(Thumbnail));
                }), TaskScheduler.Default);
            }

            return _thumbnail;
        }
    }

    /// <summary>Larger picture for the detail pane.</summary>
    public Bitmap? Preview
    {
        get
        {
            if (!_previewRequested && _entry is { IsImage: true, HasImage: true })
            {
                _previewRequested = true;
                var id = _entry.Id;
                _ = Task.Run(() => Decode(ClipService.ReadImage(id), 900)).ContinueWith(task => Ui.Post(() =>
                {
                    _preview = task.Result;
                    Raise(nameof(Preview));
                }), TaskScheduler.Default);
            }

            return _preview;
        }
    }

    /// <summary>Takes a newer state of the entry and tells the bindings.</summary>
    public void Update(ClipEntry entry)
    {
        if (ReferenceEquals(entry, _entry))
        {
            return;
        }

        var imageArrived = entry.HasImage && !_entry.HasImage;
        _entry = entry;
        if (imageArrived)
        {
            _thumbnailRequested = _previewRequested = false;
        }

        Raise(string.Empty); // every property
    }

    /// <summary>The time texts age ("Heute" becomes "Gestern"): the page asks again when it opens.</summary>
    public void RefreshTimes() => Raise(nameof(TimeText));

    private static Bitmap? Decode(byte[]? png, int width)
    {
        if (png is null)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(png);
            return Bitmap.DecodeToWidth(stream, width, BitmapInterpolationMode.HighQuality);
        }
        catch (Exception exception)
        {
            Log.Warning($"Image could not be decoded: {exception.Message}");
            return null;
        }
    }

    private static string Fold(string text, int max)
    {
        var span = text.AsSpan();
        var builder = new System.Text.StringBuilder(Math.Min(text.Length, max + 1));
        var lastWasSpace = false;
        foreach (var character in span)
        {
            if (builder.Length >= max)
            {
                builder.Append('…');
                break;
            }

            if (char.IsWhiteSpace(character))
            {
                if (!lastWasSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                lastWasSpace = true;
                continue;
            }

            builder.Append(character);
            lastWasSpace = false;
        }

        return builder.ToString().TrimEnd();
    }

    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
