using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace ThixClip;

/// <summary>The control themes of ThixClip's own lists and of the paused pause button (Themes/ThixClipControls.axaml).</summary>
public partial class ThixClipControls : ResourceDictionary
{
    public ThixClipControls()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
