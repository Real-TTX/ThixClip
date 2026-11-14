using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using ThixClip.Cloud.Business;
using ThixClip.Cloud.Controls;
using ThixClip.Shared;

namespace ThixClip.Cloud.Pages.Account;

/// <summary>The tab "Allgemein" of the account: the name and how long clips are kept (for every device of the account). Also saves the theme.</summary>
public sealed class IndexModel : AppPageModel
{
    public static IReadOnlyList<Choice> RetentionChoices { get; } = SyncContract.RetentionChoices
        .Select(d => new Choice(d.ToString(CultureInfo.InvariantCulture), AccountService.RetentionName(d), d == 0 ? "Einträge bleiben, bis du sie löschst" : null))
        .ToList();

    [BindProperty]
    public string DisplayName { get; set; } = string.Empty;

    [BindProperty]
    public int RetentionDays { get; set; } = SyncContract.DefaultRetentionDays;

    public AccountView Account { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync()
    {
        var outcome = await AccountService.GetAsync(Db, Actor);
        if (!outcome.Ok)
        {
            return NotFound();
        }

        Account = outcome.Value!;
        DisplayName = Account.DisplayName;
        RetentionDays = Account.RetentionDays;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var outcome = await AccountService.SaveGeneralAsync(Db, Actor, DisplayName, RetentionDays);
        if (!outcome.Ok)
        {
            AddError(outcome.Field, outcome.Error);
            Account = (await AccountService.GetAsync(Db, Actor)).Value!;
            return Page();
        }

        Flash("Gespeichert.");
        return RedirectToPage();
    }

    /// <summary>The theme button of the menu posts here (theme=system|light|dark).</summary>
    public async Task<IActionResult> OnPostThemeAsync(string theme)
    {
        await AccountService.SetThemeAsync(Db, Actor, theme);
        return new NoContentResult();
    }
}
