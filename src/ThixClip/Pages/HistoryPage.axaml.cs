using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ThixClip.Controls;
using ThixClip.Core.Business;
using ThixClip.Core.Models;
using ThixClip.Dialogs;
using ThixClip.Shared;

namespace ThixClip.Pages;

/// <summary>
/// The history list and the chosen entry. The page only shows and asks the business layer (ClipService); it refreshes
/// itself on ClipService.Changed (debounced) and keeps the chosen entry across refreshes. The search text comes from the
/// header of the main window, the keys from MainWindow.OnKeyDown (the search field keeps the focus).
/// </summary>
public partial class HistoryPage : UserControl
{
    private readonly Dictionary<long, ClipRowViewModel> _rows = [];
    private bool _refreshQueued;
    private List<object> _items = [];
    private ClipQuery _query = new();
    private bool _isRefreshing;
    private IDisposable? _messageTimer;
    private bool _isShowingMessage;
    private ClearRange _clearRange = ClearRange.LastHour;

    public HistoryPage()
    {
        InitializeComponent();
        KindFilter.SelectedIndex = 0;
        SortChoice.SelectedIndex = 0;
        KindFilter.SelectionChanged += (_, _) => SetQuery(_query with
        {
            Kind = KindFilter.SelectedIndex switch { 1 => ClipKind.Text, 2 => ClipKind.Link, 3 => ClipKind.Image, _ => null },
        });
        SortChoice.SelectionChanged += (_, _) => SetQuery(_query with { Sort = SortChoice.SelectedIndex == 1 ? ClipSort.OldestFirst : ClipSort.NewestFirst });

        ClipList.SelectionChanged += (_, _) => ShowSelected();
        ClipList.ContainerPrepared += (_, e) =>
        {
            var isRow = e.Index >= 0 && e.Index < _items.Count && _items[e.Index] is ClipRowViewModel;
            e.Container.IsEnabled = isRow;
            e.Container.Focusable = isRow;
        };
        ClipList.DoubleTapped += (_, e) =>
        {
            if (e.Source is Control { DataContext: ClipRowViewModel row } && row == Selected)
            {
                PasteRequested?.Invoke(this, row);
            }
        };
        ClipList.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && Selected is { } row)
            {
                e.Handled = true;
                PasteRequested?.Invoke(this, row);
            }
            else if (e.Key == Key.Delete && Selected is not null)
            {
                e.Handled = true;
                DeleteSelected(keepListFocus: true);
            }
        };

        ClipService.Changed += (_, _) => Ui.Post(QueueRefresh);
        SettingsService.Changed += (_, _) => Ui.Post(() =>
        {
            BuildKeyHints();
            if (Selected is { } row)
            {
                row.Update(row.Entry with { });
            }
        });

        // Ctrl/Cmd+click on a row or on the preview, a click on an image opens the entry in the viewer.
        ClipList.AddHandler(PointerPressedEvent, OnListPointerPressed, RoutingStrategies.Tunnel);
        PreviewFrame.PointerPressed += (_, e) =>
        {
            if ((e.KeyModifiers.HasFlag(Ui.CommandKey.Modifier) || Selected is { IsImage: true }) && e.GetCurrentPoint(PreviewFrame).Properties.IsLeftButtonPressed)
            {
                e.Handled = true;
                OpenSelected();
            }
        };

        var command = Ui.CommandKey.Name;
        ToolTip.SetTip(PasteButton, "In die vorherige App einfügen (Enter)");
        ToolTip.SetTip(CopyButton, $"In die Zwischenablage kopieren ({command}+C)");
        ToolTip.SetTip(DeleteButton, OperatingSystem.IsMacOS() ? "Löschen (⌘⌫)" : "Löschen (Strg+Entf)");

        BuildKeyHints();
        ToolTip.SetTip(ClearButton, OperatingSystem.IsMacOS()
            ? "Was in einem Zeitraum kopiert wurde löschen (⌘⇧⌫)"
            : "Was in einem Zeitraum kopiert wurde löschen (Strg+Umschalt+Entf)");
        Refresh();
        SizeChanged += (_, e) => AdaptToWidth(e.NewSize.Width);
    }

    /// <summary>
    /// A narrow window keeps the list usable: the detail pane gets smaller and goes below 820 px (Enter still pastes),
    /// the key hints go first.
    /// </summary>
    private void AdaptToWidth(double width)
    {
        var detailWidth = width < 820 ? 0 : width < 1040 ? 330 : 400;
        Columns.ColumnDefinitions[1].Width = new Avalonia.Controls.GridLength(detailWidth);
        DetailPane.IsVisible = detailWidth > 0;
        KeyHints.IsVisible = width - detailWidth > 820;
    }

    /// <summary>An entry went to the clipboard (Ctrl/Cmd+C, "Kopieren"): the main window closes when the settings say so.</summary>
    public event EventHandler? Copied;

    /// <summary>Enter, double click, "Einfügen": the main window hides and pastes.</summary>
    public event EventHandler<ClipRowViewModel>? PasteRequested;

    public ClipRowViewModel? Selected => ClipList.SelectedItem as ClipRowViewModel;

    public ClipSection Section => _query.Section;

    public void SetSection(ClipSection section) => SetQuery(_query with { Section = section });

    public void SetSearch(string text) => SetQuery(_query with { Search = text.Trim() });

    /// <summary>When the window opens: newest on top, the first entry chosen, times up to date.</summary>
    public void ResetForOpen()
    {
        Refresh();
        foreach (var row in _rows.Values)
        {
            row.RefreshTimes();
        }

        SelectRow(0);
    }

    /// <summary>Moves the choice up or down (arrow keys, page keys), skipping the group headings.</summary>
    public void MoveSelection(int delta)
    {
        var rows = _items.OfType<ClipRowViewModel>().ToList();
        if (rows.Count == 0)
        {
            return;
        }

        var current = Selected is { } selected ? rows.IndexOf(selected) : -1;
        var next = current < 0 ? (delta > 0 ? 0 : rows.Count - 1) : Math.Clamp(current + delta, 0, rows.Count - 1);
        SelectRow(next);
    }

    public void SelectFirst() => SelectRow(0);

    public void SelectLast() => SelectRow(int.MaxValue);

    /// <summary>The n-th visible entry (0-based), for Alt+1 ... Alt+9.</summary>
    public ClipRowViewModel? RowAt(int index) => _items.OfType<ClipRowViewModel>().Skip(index).FirstOrDefault();

    public void ToggleFavoriteOfSelected() => Run(() => ClipService.ToggleFavorite(RequireSelected().Id));

    public void TogglePinOfSelected() => Run(() => ClipService.TogglePin(RequireSelected().Id));

    public void CopySelected()
    {
        if (Selected is null)
        {
            return;
        }

        Run(() =>
        {
            ClipService.CopyToClipboard(RequireSelected().Id);
            ShowMessage("In die Zwischenablage kopiert.");
            Copied?.Invoke(this, EventArgs.Empty);
        });
    }

    /// <summary>
    /// Opens the chosen entry in ThixClip's own viewer (Ctrl/Cmd+O, Ctrl/Cmd+click, a click on the image, the button);
    /// pasting and copying from there work as from the list. Returns the viewer (null: nothing chosen).
    /// </summary>
    public ViewerDialog? OpenSelected()
    {
        if (Selected is not { } row || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return null;
        }

        try
        {
            return ViewerDialog.Show(owner, row, () => PasteRequested?.Invoke(this, row), CopySelected);
        }
        catch (BusinessException exception)
        {
            ShowMessage(exception.Message, isError: true);
            return null;
        }
    }

    private void OnListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(Ui.CommandKey.Modifier) || !e.GetCurrentPoint(ClipList).Properties.IsLeftButtonPressed ||
            (e.Source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not { DataContext: ClipRowViewModel row })
        {
            return;
        }

        e.Handled = true;
        ClipList.SelectedItem = row;
        OpenSelected();
    }

    /// <summary>
    /// Deletes the chosen entry and chooses the one that took its place, so the next Delete takes that one. The focus goes
    /// to the new row when it was in the list (the old row and its container are gone).
    /// </summary>
    public void DeleteSelected(bool keepListFocus = false)
    {
        if (Selected is not { } row)
        {
            return;
        }

        var rows = _items.OfType<ClipRowViewModel>().ToList();
        var index = rows.IndexOf(row);
        Run(() => ClipService.Delete(row.Id));
        Refresh();
        SelectRow(Math.Min(index, _items.OfType<ClipRowViewModel>().Count() - 1));
        if (keepListFocus && Selected is { } next)
        {
            Dispatcher.UIThread.Post(() => ClipList.ContainerFromItem(next)?.Focus(NavigationMethod.Directional), DispatcherPriority.Loaded);
        }
    }

    private void SetQuery(ClipQuery query)
    {
        if (query == _query)
        {
            return;
        }

        _query = query;
        _isShowingMessage = false; // a new search or section shows its own count at once
        Refresh();
        SelectRow(0);
    }

    /// <summary>Changes come in bursts (sync, clean-up): one refresh after the burst, when the UI thread is free again.</summary>
    private void QueueRefresh()
    {
        if (_refreshQueued)
        {
            return;
        }

        _refreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refreshQueued = false;
            Refresh();
        }, DispatcherPriority.Background);
    }

    /// <summary>Reads the list again and keeps the chosen entry (by id).</summary>
    private void Refresh()
    {
        var selectedId = Selected?.Id;
        var entries = ClipService.Query(_query);

        // Reuse the row view models: their thumbnails stay decoded.
        var alive = new HashSet<long>();
        var rows = new List<ClipRowViewModel>(entries.Count);
        foreach (var entry in entries)
        {
            alive.Add(entry.Id);
            if (_rows.TryGetValue(entry.Id, out var row))
            {
                row.Update(entry);
            }
            else
            {
                row = new ClipRowViewModel(entry);
                _rows[entry.Id] = row;
            }

            rows.Add(row);
        }

        foreach (var gone in _rows.Keys.Where(id => !alive.Contains(id)).ToList())
        {
            _rows.Remove(gone);
        }

        _items = Group(rows);
        _isRefreshing = true;
        ClipList.ItemsSource = _items;
        var restore = selectedId is { } id && _rows.TryGetValue(id, out var keep) && _items.Contains(keep) ? keep : null;
        ClipList.SelectedItem = restore;
        _isRefreshing = false;

        UpdateTexts(rows.Count);
        ShowSelected();
    }

    /// <summary>Pinned first, then by day: Heute, Gestern, Diese Woche, Dieser Monat, Älter.</summary>
    private static List<object> Group(IReadOnlyList<ClipRowViewModel> rows)
    {
        var groups = new List<(string Title, List<ClipRowViewModel> Rows)>();
        foreach (var row in rows)
        {
            var title = GroupOf(row.Entry);
            if (groups.Count == 0 || groups[^1].Title != title)
            {
                groups.Add((title, []));
            }

            groups[^1].Rows.Add(row);
        }

        var items = new List<object>(rows.Count + groups.Count);
        foreach (var (title, groupRows) in groups)
        {
            items.Add(new GroupHeaderViewModel(title, groupRows.Count) { IsFirst = items.Count == 0 });
            items.AddRange(groupRows);
        }

        return items;
    }

    private static string GroupOf(ClipEntry entry)
    {
        if (entry.IsPinned)
        {
            return "Angeheftet";
        }

        var day = entry.CopiedDate.ToLocalTime().Date;
        var today = DateTime.Today;
        if (day >= today)
        {
            return "Heute";
        }

        if (day == today.AddDays(-1))
        {
            return "Gestern";
        }

        if (day > today.AddDays(-7))
        {
            return "Diese Woche";
        }

        return day > today.AddDays(-31) ? "Dieser Monat" : "Älter";
    }

    private void UpdateTexts(int count)
    {
        TitleText.Text = _query.Section switch
        {
            ClipSection.Images => "Bilder",
            ClipSection.Favorites => "Favoriten",
            _ => "Verlauf",
        };

        // A note (copied, deleted, an error) keeps its place until its time is up, also across the refresh it caused.
        if (!_isShowingMessage)
        {
            var countText = count == 1 ? "1 Eintrag" : $"{count:N0} Einträge";
            SubtitleText.Text = _query.Search.Length > 0
                ? $"{(count == 1 ? "1 Treffer" : $"{count:N0} Treffer")} für „{_query.Search}“"
                : countText;
            SubtitleText.Foreground = Ui.Brush("TextMutedBrush");
        }

        var empty = count == 0;
        EmptyState.IsVisible = empty;
        ClipList.IsVisible = !empty;
        ClearButton.IsEnabled = ClipService.Count > 0;
        if (!empty)
        {
            return;
        }

        var hotkey = HotkeyService.Display(SettingsService.Get().Hotkey);
        (EmptyTitle.Text, EmptyText.Text, EmptyIcon.Data) = _query switch
        {
            { Search.Length: > 0 } => ($"Nichts gefunden für „{_query.Search}“", "Die Suche schaut in Texte, Links und den erkannten Text von Bildern.", Ui.Icon("Icon.Search")),
            { Section: ClipSection.Favorites } => ("Noch keine Favoriten", "Markiere einen Eintrag mit dem Stern: Favoriten bleiben dauerhaft erhalten.", Ui.Icon("Icon.Star")),
            { Section: ClipSection.Images } => ("Noch keine Bilder", "Kopierte Bilder und Screenshots erscheinen hier – ihr Text wird automatisch erkannt.", Ui.Icon("Icon.Image")),
            _ => ("Noch nichts kopiert", $"Kopiere etwas – es erscheint sofort hier. Mit {hotkey} öffnest du ThixClip jederzeit.", Ui.Icon("Icon.Clipboard")),
        };
    }

    private void ShowSelected()
    {
        if (_isRefreshing)
        {
            return;
        }

        var row = Selected;
        Detail.DataContext = row;
        DetailScroller.IsVisible = row is not null;
        NoSelection.IsVisible = row is null;
        if (row is not null)
        {
            BringIntoView(row);
        }
    }

    /// <summary>
    /// Scrolls the chosen row into view once the list has measured its new items. Asked right after the items were swapped,
    /// the virtualizing panel goes by estimated heights and leaves a gap above the first row that grows with every refresh.
    /// </summary>
    private void BringIntoView(ClipRowViewModel row) => Dispatcher.UIThread.Post(() =>
    {
        if (ReferenceEquals(Selected, row))
        {
            ClipList.ScrollIntoView(row);
        }
    }, DispatcherPriority.Loaded);

    private void SelectRow(int index)
    {
        var rows = _items.OfType<ClipRowViewModel>().ToList();
        if (rows.Count == 0)
        {
            ClipList.SelectedItem = null;
            ShowSelected();
            return;
        }

        var row = rows[Math.Clamp(index, 0, rows.Count - 1)];
        ClipList.SelectedItem = row;
        BringIntoView(row);
        DetailScroller.ScrollToHome();
    }

    private ClipRowViewModel RequireSelected() => Selected ?? throw new BusinessException("Bitte zuerst einen Eintrag auswählen.");

    private void BuildKeyHints()
    {
        var command = Ui.CommandKey.Name;
        var pastes = SettingsService.Get().PasteOnSelect;
        KeyHints.Children.Clear();
        foreach (var (keys, text) in new[]
                 {
                     ("↵", pastes ? "Einfügen" : "Kopieren"),
                     ("↑ ↓", "Auswählen"),
                     ($"{command} D", "Favorit"),
                     ($"{command} P", "Anheften"),
                     ("Esc", "Schließen"),
                 })
        {
            KeyHints.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                Margin = new Avalonia.Thickness(10, 2, 0, 2),
                Children =
                {
                    new KeyCaps { Keys = keys },
                    new TextBlock { Text = text, Theme = Ui.Theme("FaintText"), VerticalAlignment = VerticalAlignment.Center },
                },
            });
        }
    }

    /// <summary>A short note in place of the subtitle (copied, an error), then the subtitle comes back.</summary>
    public void ShowMessage(string message, bool isError = false)
    {
        SubtitleText.Text = message;
        SubtitleText.Foreground = Ui.Brush(isError ? "DangerBrush" : "SuccessBrush");
        _isShowingMessage = true;
        _messageTimer?.Dispose();
        _messageTimer = DispatcherTimer.RunOnce(() =>
        {
            _isShowingMessage = false;
            UpdateTexts(_items.OfType<ClipRowViewModel>().Count());
        }, TimeSpan.FromSeconds(2.5));
    }

    private void Run(Action action)
    {
        try
        {
            action();
        }
        catch (BusinessException exception)
        {
            ShowMessage(exception.Message, isError: true);
        }
        catch (IOException exception)
        {
            ShowMessage(exception.Message, isError: true);
        }
    }

    private void OnStarClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ClipRowViewModel row })
        {
            Run(() => ClipService.ToggleFavorite(row.Id));
        }
    }

    private void OnPasteClick(object? sender, RoutedEventArgs e)
    {
        if (Selected is { } row)
        {
            PasteRequested?.Invoke(this, row);
        }
    }

    private void OnCopyClick(object? sender, RoutedEventArgs e) => CopySelected();

    private void OnPinClick(object? sender, RoutedEventArgs e) => TogglePinOfSelected();

    private void OnFavoriteClick(object? sender, RoutedEventArgs e) => ToggleFavoriteOfSelected();

    private void OnDeleteClick(object? sender, RoutedEventArgs e) => DeleteSelected();

    private void OnOpenClick(object? sender, RoutedEventArgs e) => OpenSelected();

    private void OnCopyOcrClick(object? sender, RoutedEventArgs e) => Run(() =>
    {
        ClipService.CopyText(Selected?.OcrText ?? "");
        ShowMessage("Erkannter Text kopiert.");
    });

    /// <summary>
    /// "Verlauf löschen" as in a browser (the button below the list, Ctrl/Cmd+Shift+Delete): the dialog asks for the range,
    /// then what was copied within it goes. The last range is offered again while the app runs.
    /// </summary>
    public async Task AskToClearAsync()
    {
        if (Ui.WindowOf(this) is not { } owner || await ClearHistoryDialog.AskAsync(owner, _clearRange) is not { } range)
        {
            return;
        }

        _clearRange = range;
        Run(() =>
        {
            var count = ClipService.ClearHistory(range);
            Refresh();
            SelectRow(0);
            ShowMessage(count == 1 ? "1 Eintrag gelöscht." : $"{count:N0} Einträge gelöscht.");
        });
    }

    private async void OnClearClick(object? sender, RoutedEventArgs e) => await AskToClearAsync();
}
