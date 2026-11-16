using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MatDesktop;
using ThixClip.Controls;
using ThixClip.Core.Business;
using ThixClip.Core.Infrastructure;
using ThixClip.Core.Models;
using ThixClip.Pages;

namespace ThixClip.Dialogs;

/// <summary>
/// "Öffnen" of an entry, inside ThixClip (no other program, no file with clipboard contents on the disk): an image as
/// large as the window allows or at 100 % (Space switches, scroll bars at 100 %), a text or link whole and selectable.
/// Enter pastes, Ctrl/Cmd+C copies (the selection, when some text is selected), a link goes to the browser only on
/// request, Esc closes. The caller does the pasting and copying (as from the list), so the rules stay in one place.
/// </summary>
public sealed class ViewerDialog : MatWindow
{
    private readonly Action _paste;
    private readonly Action _copy;
    private readonly Image? _image;
    private readonly ScrollViewer _scroller;
    private readonly SelectableTextBlock? _text;
    private readonly TextBlock? _zoomLabel;
    private bool _actualSize;
    private bool _closed;

    private ViewerDialog(ClipRowViewModel row, Action paste, Action copy)
    {
        _paste = paste;
        _copy = copy;
        Title = row.IsImage ? "Bild ansehen" : row.IsLink ? "Link ansehen" : "Text ansehen";
        ShowInTaskbar = false;
        MinWidth = 420;
        MinHeight = 300;
        Icon = Ui.AppIcon;
        this.Use(BackgroundProperty, "Mat.Surface");

        var heading = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children =
            {
                new MatIcon { Icon = row.KindIcon, Size = 20 },
                new TextBlock { Text = row.TypeText, FontSize = MatTokens.Type.FsLg, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock
                {
                    Text = $"{row.SizeText} · kopiert {row.CopiedText}",
                    VerticalAlignment = VerticalAlignment.Center,
                }.Use(TextBlock.ForegroundProperty, "Mat.TextMuted"),
            },
        };

        _scroller = new ScrollViewer
        {
            // Focusable: the keys (arrows scroll, Space, Enter, Esc) belong to the viewer, not to the window below.
            Focusable = true,
            FocusAdorner = null,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        var frame = new Border
        {
            ClipToBounds = true,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 14, 0, 18),
            Child = _scroller,
        };
        frame.Use(Border.BorderBrushProperty, "Mat.Border").Use(Border.BackgroundProperty, "Mat.Surface2").Use(Border.CornerRadiusProperty, "Mat.RadiusLg");

        var pasteButton = new Button { Content = new IconLabel { Icon = "paste", Text = "Einfügen" }, Theme = Ui.Theme("MatButton.Primary") };
        pasteButton.Click += (_, _) => Paste();
        ToolTip.SetTip(pasteButton, "In die vorherige App einfügen (Enter)");

        var copyButton = new Button { Content = new IconLabel { Icon = "copy", Text = "Kopieren" } };
        copyButton.Click += (_, _) => Copy();
        ToolTip.SetTip(copyButton, $"In die Zwischenablage kopieren ({Ui.CommandKey.Name}+C)");

        var actions = new ActionBar { Children = { pasteButton, copyButton } };

        if (row.IsImage)
        {
            _image = new Image { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(12) };
            RenderOptions.SetBitmapInterpolationMode(_image, BitmapInterpolationMode.HighQuality);
            AutomationProperties.SetName(_image, row.ImageCaption);
            _image.DoubleTapped += (_, _) => ToggleZoom();
            _scroller.Content = _image;
            _scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            _scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;

            _zoomLabel = new TextBlock();
            var zoom = new Button { Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new MatIcon { Icon = "search" }, _zoomLabel } } };
            zoom.Click += (_, _) => ToggleZoom();
            ToolTip.SetTip(zoom, "Einpassen / Originalgröße (Leertaste oder Doppelklick)");
            actions.Children.Add(zoom);
            LoadImage(row.Id);
        }
        else
        {
            _text = new SelectableTextBlock
            {
                Text = row.FullText,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(18, 16),
                FontSize = 14,
                LineHeight = 21,
            };
            AutomationProperties.SetName(_text, row.TypeText);
            _scroller.Content = _text;

            if (ClipService.BrowserLink(row.Id) is { } link)
            {
                var browser = new Button { Content = new IconLabel { Icon = "external", Text = "Im Browser öffnen" } };
                browser.Click += (_, _) =>
                {
                    Ui.OpenExternal(link);
                    Close();
                };
                actions.Children.Add(browser);
            }
        }

        var close = new Button { Content = new IconLabel { Icon = "x", Text = "Schließen" }, IsCancel = true };
        close.Click += (_, _) => Close();
        ActionBar.SetSeparated(close, true);
        actions.Children.Add(close);

        var layout = new DockPanel { Margin = new Thickness(24, 20, 24, 22) };
        DockPanel.SetDock(heading, Dock.Top);
        DockPanel.SetDock(actions, Dock.Bottom);
        layout.Children.Add(heading);
        layout.Children.Add(actions);
        layout.Children.Add(frame);
        Content = layout;

        AddHandler(KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Opened += (_, _) => Dispatcher.UIThread.Post(() => _scroller.Focus(NavigationMethod.Directional));
        Closed += (_, _) =>
        {
            _closed = true;
            (_image?.Source as IDisposable)?.Dispose();
        };
        UpdateZoom();
    }

    /// <summary>Text shown, when this is a text or link (tests).</summary>
    public string? ShownText => _text?.Text;

    /// <summary>True once the whole image is decoded and shown (tests).</summary>
    public bool ShowsImage => _image?.Source is not null;

    public bool IsActualSize => _actualSize;

    /// <summary>
    /// Opens the viewer over <paramref name="owner"/>; <paramref name="paste"/> and <paramref name="copy"/> run after it
    /// closed (pasting hides ThixClip and goes back to the app before).
    /// </summary>
    public static ViewerDialog Show(Window owner, ClipRowViewModel row, Action paste, Action copy)
    {
        var dialog = new ViewerDialog(row, paste, copy) { WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var area = owner.Bounds.Size;
        dialog.Width = Math.Max(dialog.MinWidth, Math.Min(1100, area.Width - 60));
        dialog.Height = Math.Max(dialog.MinHeight, Math.Min(820, area.Height - 40));
        _ = dialog.ShowDialog(owner);
        return dialog;
    }

    public void ToggleZoom()
    {
        if (_image is null)
        {
            return;
        }

        _actualSize = !_actualSize;
        UpdateZoom();
    }

    private void UpdateZoom()
    {
        if (_image is null || _zoomLabel is null)
        {
            return;
        }

        _image.Stretch = _actualSize ? Stretch.None : Stretch.Uniform;
        var bars = _actualSize ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        _scroller.HorizontalScrollBarVisibility = bars;
        _scroller.VerticalScrollBarVisibility = bars;
        _image.HorizontalAlignment = HorizontalAlignment.Center;
        _image.VerticalAlignment = VerticalAlignment.Center;
        _zoomLabel.Text = _actualSize ? "Einpassen" : "Originalgröße";
    }

    private void LoadImage(long id)
    {
        byte[] png;
        try
        {
            png = ClipService.ReadImageToView(id);
        }
        catch (BusinessException exception)
        {
            _scroller.Content = new TextBlock
            {
                Text = exception.Message,
                Theme = Ui.Theme("MatText.Muted"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                using var stream = new MemoryStream(png);
                return new Bitmap(stream);
            }
            catch (Exception exception)
            {
                Log.Warning($"Image could not be decoded for the viewer: {exception.Message}");
                return null;
            }
        }).ContinueWith(task => Ui.Post(() =>
        {
            if (_closed)
            {
                task.Result?.Dispose();
                return;
            }

            _image!.Source = task.Result;
        }), TaskScheduler.Default);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var withCommand = e.KeyModifiers.HasFlag(Ui.CommandKey.Modifier);
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                Close();
                break;
            case Key.Enter when !withCommand:
                e.Handled = true;
                Paste();
                break;
            case Key.C when withCommand:
                // A selection inside the text is copied by the text itself.
                if (_text is { SelectionStart: var start, SelectionEnd: var end } && start != end)
                {
                    return;
                }

                e.Handled = true;
                Copy();
                break;
            case Key.Space when _image is not null:
                e.Handled = true;
                ToggleZoom();
                break;
        }
    }

    private void Paste()
    {
        Close();
        _paste();
    }

    private void Copy()
    {
        Close();
        _copy();
    }
}
