using Microsoft.AspNetCore.Mvc;
using ThixClip.Cloud.Business;
using ThixClip.Cloud.Controls;
using ThixClip.Shared;

namespace ThixClip.Cloud.Pages.Clips;

/// <summary>
/// One clip: without an id a new one (a text, or a picture to upload: it appears on every device), with an id the clip itself (the text can be changed,
/// favourite and pinned, copy, download, delete). Fields that make no sense for the kind are not there (no text field for a picture).
/// </summary>
public sealed class EditModel : AppPageModel
{
    public static readonly IReadOnlyList<Choice> Kinds = [new("Text", "Text oder Link"), new("Image", "Bild")];

    [BindProperty(SupportsGet = true)]
    public long? Id { get; set; }

    [BindProperty]
    public string Kind { get; set; } = "Text";

    [BindProperty]
    public string Text { get; set; } = string.Empty;

    [BindProperty]
    public IFormFile? Image { get; set; }

    [BindProperty]
    public bool IsFavorite { get; set; }

    [BindProperty]
    public bool IsPinned { get; set; }

    public ClipDetail? Clip { get; private set; }

    public bool IsNew => Id is null;

    public async Task<IActionResult> OnGetAsync()
    {
        if (IsNew)
        {
            return Page();
        }

        return await LoadAsync() ? Page() : NotFound();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (IsNew)
        {
            return await CreateAsync();
        }

        var outcome = await ClipService.UpdateAsync(Db, Actor, Id!.Value, Text, IsFavorite, IsPinned);
        if (outcome.NotFound)
        {
            return NotFound();
        }

        if (!outcome.Ok)
        {
            AddError(outcome.Field, outcome.Error);
            await LoadAsync(keepInput: true);
            return Page();
        }

        Flash("Gespeichert – auf allen Geräten.");
        return RedirectToPage(new { id = Id });
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (IsNew)
        {
            return NotFound();
        }

        var outcome = await ClipService.DeleteAsync(Db, Actor, Id!.Value);
        if (outcome.NotFound)
        {
            return NotFound();
        }

        Flash("Eintrag gelöscht – auch auf allen Geräten.");
        return Redirect("/Clips");
    }

    private async Task<IActionResult> CreateAsync()
    {
        Outcome<long> outcome;
        if (Kind == "Image")
        {
            if (Image is null || Image.Length == 0)
            {
                AddError(nameof(Image), "Bitte ein Bild auswählen.");
                return Page();
            }

            if (Image.Length > SyncContract.MaxImageBytes)
            {
                AddError(nameof(Image), $"Das Bild ist größer als {SyncContract.MaxImageBytes / 1024 / 1024} MB.");
                return Page();
            }

            using var buffer = new MemoryStream();
            await Image.CopyToAsync(buffer);
            outcome = await ClipService.CreateImageAsync(Db, Actor, buffer.ToArray());
        }
        else
        {
            outcome = await ClipService.CreateTextAsync(Db, Actor, Text);
        }

        if (!outcome.Ok)
        {
            AddError(outcome.Field, outcome.Error);
            return Page();
        }

        Flash("Eintrag angelegt – er ist jetzt auf allen Geräten.");
        return RedirectToPage(new { id = outcome.Value });
    }

    private async Task<bool> LoadAsync(bool keepInput = false)
    {
        var outcome = await ClipService.GetAsync(Db, Actor, Id!.Value);
        if (!outcome.Ok)
        {
            return false;
        }

        Clip = outcome.Value!;
        if (!keepInput)
        {
            Text = Clip.Text ?? string.Empty;
            IsFavorite = Clip.IsFavorite;
            IsPinned = Clip.IsPinned;
        }

        return true;
    }
}
