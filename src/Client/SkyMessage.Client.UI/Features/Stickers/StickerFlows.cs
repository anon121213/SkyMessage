using SkyMessage.Client.Core.Stores;
using SkyMessage.Client.UI.Services;
using SkyMessage.Contracts;

namespace SkyMessage.Client.UI.Features.Stickers;

/// <summary>Steps several screens share: making a pack (its title first, then its editor) and renaming one.</summary>
public static class StickerFlows
{
    /// <summary>Asks for a title, makes the pack and opens its editor; false when cancelled or refused.</summary>
    public static async Task<bool> CreatePackAsync(ILocalizer l, DialogService dialogs, IStickerStore stickers, NavigationService nav, ToastService toasts)
    {
        var title = await dialogs.PromptAsync(TitlePrompt(l, l["stickers.create.title"], null, l["stickers.create.confirm"]));
        if (title is null) return false;
        try
        {
            var pack = await stickers.CreatePackAsync(StickerRules.NormalizeTitle(title));
            nav.NavigateTo(StickerUi.EditorPath(pack.Id));
            return true;
        }
        catch (Exception ex)
        {
            toasts.Error(ex);
            return false;
        }
    }

    public static async Task RenamePackAsync(ILocalizer l, DialogService dialogs, IStickerStore stickers, ToastService toasts, StickerPackDto pack)
    {
        var title = await dialogs.PromptAsync(TitlePrompt(l, l["stickers.edit.rename"], pack.Title, l["common.save"]));
        if (title is null || StickerRules.NormalizeTitle(title) == pack.Title) return;
        try
        {
            await stickers.RenamePackAsync(pack.Id, StickerRules.NormalizeTitle(title));
        }
        catch (Exception ex)
        {
            toasts.Error(ex);
        }
    }

    private static PromptOptions TitlePrompt(ILocalizer l, string title, string? initial, string confirm) => new()
    {
        Title = title,
        Label = l["stickers.create.label"],
        Placeholder = l["stickers.create.placeholder"],
        InitialValue = initial,
        MaxLength = Limits.StickerPackTitleMaxLength,
        ConfirmText = confirm,
        Validate = value => StickerRules.IsValidTitle(StickerRules.NormalizeTitle(value)) ? null : l["stickers.create.invalid"],
    };
}
