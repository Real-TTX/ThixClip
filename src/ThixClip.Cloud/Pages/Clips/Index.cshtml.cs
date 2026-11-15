using Microsoft.AspNetCore.Mvc;
using ThixClip.Cloud.Business;
using ThixClip.Cloud.Web;
using ThixClip.Shared;

namespace ThixClip.Cloud.Pages.Clips;

/// <summary>"Verlauf": the clips of the account, newest first, grouped by day; search (also the text recognised in pictures), filter and sort above, pages below.</summary>
public sealed class IndexModel : AppPageModel
{
    public ToolbarModel Toolbar { get; private set; } = null!;

    public PagerModel Pager { get; private set; } = null!;

    public ClipListResult Result { get; private set; } = null!;

    /// <summary>The rows under their day ("Heute", "Gestern" ...) while the list is ordered by a date; else one group without a heading.</summary>
    public IReadOnlyList<(string? Heading, IReadOnlyList<ClipListItem> Items)> Groups { get; private set; } = [];

    public string Here => Request.Path + Request.QueryString;

    public async Task OnGetAsync()
    {
        var search = ListQuery.Text(Request, "q");
        var kind = ListQuery.Choice(Request, "kind", "text", "link", "image");
        var flag = ListQuery.Choice(Request, "flag", "favorite", "pinned");
        var sort = ListQuery.Choice(Request, "sort", "copied", "created") ?? "copied";
        var descending = ListQuery.Descending(Request, byDefault: true);
        var page = ListQuery.Page(Request);

        Result = await ClipService.ListAsync(Db, Actor, new ClipQuery(search, kind, flag, sort, descending, page, ClipService.DefaultPageSize));
        Toolbar = new ToolbarModel
        {
            Id = "clips",
            Action = "/Clips",
            Label = "Verlauf",
            Search = search,
            SearchPlaceholder = "Zwischenablage durchsuchen (auch Text aus Bildern)",
            Filters =
            [
                new FilterField("kind", "Typ", [new("text", "Text"), new("link", "Link"), new("image", "Bild")], kind),
                new FilterField("flag", "Markiert", [new("favorite", "Favoriten"), new("pinned", "Angeheftet")], flag),
            ],
            SortOptions = [new SortOption("copied", "Zuletzt kopiert"), new SortOption("created", "Zuerst kopiert")],
            Sort = sort,
            Descending = descending,
            DefaultSort = "copied",
            DefaultDescending = true,
            Total = Result.Total,
            Matching = Result.Matching,
        };
        Pager = new PagerModel { Page = page, PageSize = ClipService.DefaultPageSize, Total = Result.Matching, Url = Toolbar.UrlWithPage };

        Groups = Result.Items
            .GroupBy(c => Dates.DayKey(sort == "created" ? c.CreatedDate : c.CopiedDate))
            .Select(g => ((string?)Dates.DayHeading(sort == "created" ? g.First().CreatedDate : g.First().CopiedDate), (IReadOnlyList<ClipListItem>)g.ToList()))
            .ToList();
    }

    /// <summary>The star of a row.</summary>
    public async Task<IActionResult> OnPostFavoriteAsync(long id, bool on, string? returnUrl)
    {
        var outcome = await ClipService.SetFavoriteAsync(Db, Actor, id, on);
        if (outcome.NotFound)
        {
            return NotFound();
        }

        return Back(returnUrl, "/Clips");
    }

    /// <summary>"Verlauf löschen" (the dialog below the list): the clips copied within the range go, except favourites and pinned ones, on every device.</summary>
    public async Task<IActionResult> OnPostClearAsync(ClearRange range)
    {
        var removed = await ClipService.ClearHistoryAsync(Db, Actor, range);
        var when = ClearRanges.Text(range);
        Flash(removed == 0 ? $"{when}: es gab nichts zu löschen." : removed == 1 ? $"{when}: 1 Eintrag gelöscht." : $"{when}: {Ui.Number(removed)} Einträge gelöscht.");
        return RedirectToPage();
    }
}
