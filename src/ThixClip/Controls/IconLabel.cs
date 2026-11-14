using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace ThixClip.Controls;

/// <summary>Icon and text side by side: the content of most buttons ("Einfügen", "Kopieren" ...).</summary>
public sealed class IconLabel : StackPanel
{
    public static readonly StyledProperty<Geometry?> IconProperty = AvaloniaProperty.Register<IconLabel, Geometry?>(nameof(Icon));

    public static readonly StyledProperty<string> TextProperty = AvaloniaProperty.Register<IconLabel, string>(nameof(Text), "");

    public static readonly StyledProperty<bool> IsIconFilledProperty = AvaloniaProperty.Register<IconLabel, bool>(nameof(IsIconFilled));

    private readonly IconView _icon = new() { Width = 16, Height = 16 };
    private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center };

    static IconLabel()
    {
        IconProperty.Changed.AddClassHandler<IconLabel>((label, _) => label.Refresh());
        TextProperty.Changed.AddClassHandler<IconLabel>((label, _) => label.Refresh());
        IsIconFilledProperty.Changed.AddClassHandler<IconLabel>((label, _) => label.Refresh());
    }

    public IconLabel()
    {
        Orientation = Orientation.Horizontal;
        Spacing = 8;
        Children.Add(_icon);
        Children.Add(_text);
        Refresh();
    }

    public Geometry? Icon
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

    private void Refresh()
    {
        _icon.Data = Icon;
        _icon.IsFilled = IsIconFilled;
        _icon.IsVisible = Icon is not null;
        _text.Text = Text;
        _text.IsVisible = Text.Length > 0;
    }
}
