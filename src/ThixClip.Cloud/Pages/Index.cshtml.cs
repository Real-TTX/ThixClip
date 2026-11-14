using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ThixClip.Cloud.Pages;

/// <summary>The start address leads to the history (the sign-in asks first when nobody is signed in).</summary>
public sealed class IndexModel : PageModel
{
    public IActionResult OnGet() => Redirect("/Clips");
}
