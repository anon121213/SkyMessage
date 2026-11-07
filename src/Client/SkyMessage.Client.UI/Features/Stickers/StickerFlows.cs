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

    /// <summary>
    /// Takes a pack out of my panel with an Undo that puts it back where it was (adding goes first, so the old order is
    /// asked for again). <paramref name="held"/> is the pack when its stickers are known (else it is loaded for the undo).
    /// </summary>
    public static async Task RemoveFromPanelAsync(ILocalizer l, IStickerStore stickers, ToastService toasts, Guid packId, string title,
        StickerPackDto? held = null)
    {
        var order = stickers.State.Installed.Select(p => p.Id).ToList();
        try
        {
            await stickers.UninstallAsync(packId);
        }
        catch (Exception ex)
        {
            toasts.Error(ex);
            return;
        }
        toasts.Show(l.Format("stickers.pack.removed", title), ToastKind.Info, l["common.undo"], async () =>
        {
            try
            {
                var pack = held ?? await stickers.LoadPackAsync(packId);
                await stickers.InstallAsync(pack);
                var now = stickers.State.Installed.Select(p => p.Id).ToHashSet();
                var previous = order.Where(now.Contains).ToList();
                if (previous.Count > 1) await stickers.ReorderAsync(previous);
            }
            catch (Exception ex)
            {
                toasts.Error(ex);
            }
        });
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
