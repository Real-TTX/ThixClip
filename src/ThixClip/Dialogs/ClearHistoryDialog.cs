using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MatDesktop;
using ThixClip.Controls;
using ThixClip.Core.Business;
using ThixClip.Shared;

namespace ThixClip.Dialogs;

/// <summary>
/// "Verlauf löschen" as in a browser: the range (letzte Stunde ... gesamte Zeit) with the number of entries each would
/// delete. ↑ ↓ choose, Enter deletes, Esc cancels. Favourites and pinned entries stay. Answers the range or null; the
/// caller deletes (ClipService.ClearHistory).
/// </summary>
public sealed class ClearHistoryDialog : MatWindow
{
    private readonly ListBox _ranges = new()
    {
        SelectionMode = SelectionMode.Single | SelectionMode.AlwaysSelected,
        Margin = new Thickness(-4, 0, -4, 0),
    };

    private readonly Button _delete;
    private readonly IconLabel _deleteLabel = new() { Icon = "trash" };
    private ClearRange? _chosen;

    private ClearHistoryDialog(ClearRange initial)
    {
        Title = "Verlauf löschen";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        this.Use(BackgroundProperty, "Mat.Surface");

        _ranges.Theme = Ui.Theme("MatList");
        AutomationProperties.SetName(_ranges, "Zeitraum");
        foreach (var range in ClearRanges.Choices)
        {
            _ranges.Items.Add(RangeItem(range, ClipService.CountToClear(range)));
        }

        _ranges.SelectedIndex = Math.Max(0, ClearRanges.Choices.ToList().IndexOf(initial));
        _ranges.SelectionChanged += (_, _) => UpdateDeleteButton();

        // Enter deletes the chosen range (the dialog is the question already), Esc cancels.
        _delete = new Button { Content = _deleteLabel, IsDefault = true, Theme = Ui.Theme("MatButton.Danger") };
        _delete.Click += (_, _) =>
        {
            _chosen = Selected.Range;
            Close();
        };

        var cancel = new Button { Content = new IconLabel { Icon = "x", Text = "Abbrechen" }, IsCancel = true };
        cancel.Click += (_, _) => Close();

        var heading = new TextBlock { Text = Title, FontSize = MatTokens.Type.FsLg, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 14) };
        var label = new TextBlock { Text = "Zeitraum", Theme = Ui.Theme("MatText.FieldLabel"), Margin = new Thickness(0, 0, 0, 6) };
        var hint = new TextBlock
        {
            Text = SettingsService.Get().Cloud.IsConnected
                ? "Gelöscht wird, was im Zeitraum zuletzt kopiert wurde – auch auf deinen anderen Geräten und in der Cloud. Favoriten und angeheftete Einträge bleiben."
                : "Gelöscht wird, was im Zeitraum zuletzt kopiert wurde. Favoriten und angeheftete Einträge bleiben.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 22),
        }.Use(TextBlock.ForegroundProperty, "Mat.TextMuted");

        Content = new StackPanel
        {
            Margin = new Thickness(24, 22),
            Children = { heading, label, _ranges, hint, new ActionBar { Children = { _delete, cancel } } },
        };

        UpdateDeleteButton();
        Opened += (_, _) => Dispatcher.UIThread.Post(() => (_ranges.SelectedItem as ListBoxItem)?.Focus(NavigationMethod.Directional));
    }

    /// <summary>The range the user chose; null when they cancelled.</summary>
    public static async Task<ClearRange?> AskAsync(Window owner, ClearRange initial)
    {
        var dialog = new ClearHistoryDialog(initial);
        await dialog.ShowDialog(owner);
        return dialog._chosen;
    }

    private RangeChoice Selected => (RangeChoice)((ListBoxItem)_ranges.SelectedItem!).Tag!;

    private void UpdateDeleteButton()
    {
        var count = Selected.Count;
        _delete.IsEnabled = count > 0;
        _deleteLabel.Text = count switch
        {
            0 => "Nichts zu löschen",
            1 => "1 Eintrag löschen",
            _ => $"{count:N0} Einträge löschen",
        };
    }

    private static ListBoxItem RangeItem(ClearRange range, int count)
    {
        var name = new TextBlock { Text = ClearRanges.Text(range), VerticalAlignment = VerticalAlignment.Center };
        var number = new TextBlock
        {
            Text = count switch { 0 => "nichts", 1 => "1 Eintrag", _ => $"{count:N0} Einträge" },
            Theme = Ui.Theme("MatText.Muted"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(number, 1);

        var item = new ListBoxItem
        {
            Theme = Ui.Theme("MatChoiceRow"),
            Tag = new RangeChoice(range, count),
            Content = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { name, number } },
        };
        AutomationProperties.SetName(item, $"{ClearRanges.Text(range)}, {number.Text}");
        return item;
    }

    private sealed record RangeChoice(ClearRange Range, int Count);
}
