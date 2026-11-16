using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using MatDesktop;
using Path = Avalonia.Controls.Shapes.Path;

namespace ThixClip.Controls;

/// <summary>
/// Icon and text side by side: the content of most buttons ("Einfügen", "Kopieren" ...). <see cref="Icon"/> is the name of
/// an icon of MatDesktop's sprite (DESK-09). <see cref="IsIconFilled"/> fills it (a chosen favourite, a pinned entry):
/// MatIcon only strokes, so the filled one is the same geometry of the sprite as a shape. <see cref="IsTextVisible"/> false
/// leaves the icon alone; the button's tool tip then names it.
/// </summary>
public sealed class IconLabel : StackPanel
{
    public static readonly StyledProperty<string?> IconProperty = AvaloniaProperty.Register<IconLabel, string?>(nameof(Icon));

    public static readonly StyledProperty<string> TextProperty = AvaloniaProperty.Register<IconLabel, string>(nameof(Text), "");

    public static readonly StyledProperty<bool> IsIconFilledProperty = AvaloniaProperty.Register<IconLabel, bool>(nameof(IsIconFilled));

    public static readonly StyledProperty<bool> IsTextVisibleProperty = AvaloniaProperty.Register<IconLabel, bool>(nameof(IsTextVisible), true);

    public static readonly StyledProperty<double> IconSizeProperty = AvaloniaProperty.Register<IconLabel, double>(nameof(IconSize), 16);

    private readonly MatIcon _icon = new();
    private readonly Viewbox _filled = new() { IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
    private readonly Path _shape = new() { Width = 24, Height = 24, Stretch = Stretch.None, StrokeThickness = 2, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round };
    private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center };

    static IconLabel()
    {
        IconProperty.Changed.AddClassHandler<IconLabel>((label, _) => label.Refresh());
        TextProperty.Changed.AddClassHandler<IconLabel>((label, _) => label.Refresh());
        IsIconFilledProperty.Changed.AddClassHandler<IconLabel>((label, _) => label.Refresh());
        IsTextVisibleProperty.Changed.AddClassHandler<IconLabel>((label, _) => label.Refresh());
        IconSizeProperty.Changed.AddClassHandler<IconLabel>((label, _) => label.Refresh());
    }

    public IconLabel()
    {
        Orientation = Orientation.Horizontal;
        Spacing = 8;

        // the filled shape takes the colour of the text around it, like the stroked icon
        var foreground = this.GetObservable(TextElement.ForegroundProperty);
        _shape.Bind(Shape.FillProperty, foreground);
        _shape.Bind(Shape.StrokeProperty, foreground);
        _filled.Child = _shape;
        Children.Add(_icon);
        Children.Add(_filled);
        Children.Add(_text);
        Refresh();
    }

    /// <summary>The name of the icon in the sprite ("copy", "trash" ...).</summary>
    public string? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public bool IsIconFilled
    {
        get => GetValue(IsIconFilledProperty);
        set => SetValue(IsIconFilledProperty, value);
    }

    public bool IsTextVisible
    {
        get => GetValue(IsTextVisibleProperty);
        set => SetValue(IsTextVisibleProperty, value);
    }

    public double IconSize
    {
        get => GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    protected override void OnAttachedToLogicalTree(Avalonia.LogicalTree.LogicalTreeAttachmentEventArgs e)
    {
        base.OnAttachedToLogicalTree(e);
        Refresh();
    }

    private void Refresh()
    {
        var hasIcon = !string.IsNullOrEmpty(Icon);
        _icon.Icon = Icon;
        _icon.Size = IconSize;
        _icon.IsVisible = hasIcon && !IsIconFilled;
        _filled.Width = _filled.Height = IconSize;
        _shape.Data = hasIcon && IsIconFilled ? MatIcon.Find(Icon!) : null;
        _filled.IsVisible = hasIcon && IsIconFilled;
        _text.Text = Text;
        _text.IsVisible = IsTextVisible && Text.Length > 0;
    }
}
