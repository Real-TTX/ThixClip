using ThixClip.Cloud.Business;
using ThixClip.Cloud.Web;

namespace ThixClip.Cloud.Pages.Account;

/// <summary>The tab "Geräte": the desktop apps signed in to the account. A device is added by signing in in the app; it is removed on its own page.</summary>
public sealed class DevicesModel : AppPageModel
{
    public ToolbarModel Toolbar { get; private set; } = null!;

    public IReadOnlyList<DeviceView> Items { get; private set; } = [];

    public async Task OnGetAsync()
    {
        var search = ListQuery.Text(Request, "q");
        var sort = ListQuery.Choice(Request, "sort", "seen", "name", "created") ?? "seen";
        var descending = ListQuery.Descending(Request, byDefault: sort != "name");
        var (items, total) = await DeviceService.ListAsync(Db, Actor, search, sort, descending);
        Items = items;
        Toolbar = new ToolbarModel
        {
            Id = "devices",
            Action = "/Account/Devices",
            Label = "Geräte",
            Search = search,
            SearchPlaceholder = "Geräte durchsuchen",
            SortOptions = [new SortOption("seen", "Zuletzt gesehen"), new SortOption("name", "Name"), new SortOption("created", "Angemeldet seit")],
            Sort = sort,
            Descending = descending,
            DefaultSort = "seen",
            DefaultDescending = true,
            Total = total,
            Matching = items.Count,
            Noun = "Geräte",
            NounOne = "Gerät",
        };
    }
}
