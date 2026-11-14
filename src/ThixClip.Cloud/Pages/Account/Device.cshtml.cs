using Microsoft.AspNetCore.Mvc;
using ThixClip.Cloud.Business;

namespace ThixClip.Cloud.Pages.Account;

/// <summary>One device of the account: its name can be changed, and it can be removed (its token ends: the app has to sign in again).</summary>
public sealed class DeviceModel : AppPageModel
{
    [BindProperty(SupportsGet = true)]
    public long Id { get; set; }

    [BindProperty]
    public string Name { get; set; } = string.Empty;

    public DeviceView Device { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync()
    {
        if (!await LoadAsync())
        {
            return NotFound();
        }

        Name = Device.Name;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var outcome = await DeviceService.RenameAsync(Db, Actor, Id, Name);
        if (outcome.NotFound)
        {
            return NotFound();
        }

        if (!outcome.Ok)
        {
            AddError(outcome.Field, outcome.Error);
            await LoadAsync();
            return Page();
        }

        Flash("Gespeichert.");
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        var outcome = await DeviceService.RemoveAsync(Db, Actor, Id);
        if (outcome.NotFound)
        {
            return NotFound();
        }

        Flash("Das Gerät wurde entfernt. Es synchronisiert nicht mehr, bis es sich neu anmeldet.");
        return Redirect("/Account/Devices");
    }

    private async Task<bool> LoadAsync()
    {
        var outcome = await DeviceService.GetAsync(Db, Actor, Id);
        if (!outcome.Ok)
        {
            return false;
        }

        Device = outcome.Value!;
        return true;
    }
}
