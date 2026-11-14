using Microsoft.AspNetCore.Mvc;
using ThixClip.Cloud.Web;

namespace ThixClip.Cloud.Pages.Account;

/// <summary>Signs the browser out (only by a form, so another site cannot do it with a link).</summary>
public sealed class LogoutModel : AppPageModel
{
    public IActionResult OnGet() => Redirect("/Clips");

    public async Task<IActionResult> OnPostAsync()
    {
        await CookieSignIn.SignOutAsync(HttpContext, Db);
        return Redirect("/Account/Login?out=true");
    }
}
