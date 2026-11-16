using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using MatDesktop;
using ThixClip.Controls;
using ThixClip.Core.Business;

namespace ThixClip.Dialogs;

/// <summary>
/// "Aufzeichnung pausieren": how long (15 min, 30 min, 1 hour, until ThixClip starts again, until I resume). ↑ ↓ choose,
/// Enter pauses, Esc cancels. Answers the length or null; the caller pauses (RecordingService.Pause).
/// </summary>
public sealed class PauseDialog : MatWindow
{
    private readonly ListBox _lengths = new()
    {
        SelectionMode = SelectionMode.Single | SelectionMode.AlwaysSelected,
        Margin = new Thickness(-4, 0, -4, 0),
    };

    private PauseLength? _chosen;

    private PauseDialog(PauseLength initial)
    {
        Title = "Aufzeichnung pausieren";
        Width = 400;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        this.Use(BackgroundProperty, "Mat.Surface");

        _lengths.Theme = Ui.Theme("MatList");
        AutomationProperties.SetName(_lengths, "Dauer");
        foreach (var length in RecordingService.Choices)
        {
            var item = new ListBoxItem
            {
                Theme = Ui.Theme("MatChoiceRow"),
                Tag = length,
                Content = new TextBlock { Text = RecordingService.Text(length) },
            };
            AutomationProperties.SetName(item, RecordingService.Text(length));
            item.DoubleTapped += (_, _) => Choose();
            _lengths.Items.Add(item);
        }

        _lengths.SelectedIndex = Math.Max(0, RecordingService.Choices.ToList().IndexOf(initial));

        var pause = new Button
        {
            Content = new IconLabel { Icon = "pause", Text = "Pausieren" },
            IsDefault = true,
            Theme = Ui.Theme("MatButton.Primary"),
        };
        pause.Click += (_, _) => Choose();

        var cancel = new Button { Content = new IconLabel { Icon = "x", Text = "Abbrechen" }, IsCancel = true };
        cancel.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(24, 22),
            Children =
            {
                new TextBlock { Text = Title, FontSize = MatTokens.Type.FsLg, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 6) },
                new TextBlock
                {
                    Text = "Solange die Pause läuft, kommt nichts Kopiertes in den Verlauf.",
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 14),
                }.Use(TextBlock.ForegroundProperty, "Mat.TextMuted"),
                new TextBlock { Text = "Dauer", Theme = Ui.Theme("MatText.FieldLabel"), Margin = new Thickness(0, 0, 0, 6) },
                _lengths,
                new ActionBar { Margin = new Thickness(0, 20, 0, 0), Children = { pause, cancel } },
            },
        };

        Opened += (_, _) => Dispatcher.UIThread.Post(() => (_lengths.SelectedItem as ListBoxItem)?.Focus(NavigationMethod.Directional));
    }

    /// <summary>The length the user chose; null when they cancelled. Without an owner (tray) it opens in the middle of the screen.</summary>
    public static async Task<PauseLength?> AskAsync(Window? owner, PauseLength initial)
    {
        var dialog = new PauseDialog(initial);
        if (owner is { IsVisible: true })
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            await dialog.ShowDialog(owner);
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.Topmost = true;
            var closed = new TaskCompletionSource();
            dialog.Closed += (_, _) => closed.TrySetResult();
            dialog.Show();
            dialog.Activate();
            await closed.Task;
        }

        return dialog._chosen;
    }

    private void Choose()
    {
        _chosen = (PauseLength)((ListBoxItem)_lengths.SelectedItem!).Tag!;
        Close();
    }
}
