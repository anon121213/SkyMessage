using Microsoft.AspNetCore.Mvc;
using SkyMessage.Application.Stickers;
using SkyMessage.Contracts;

namespace SkyMessage.Api.Controllers;

/// <summary>Sticker packs, my stickers and saved GIFs (SPEC §6.6).</summary>
[ApiController]
public sealed class StickersController(IStickerService stickers, IStickerPackService packs) : ControllerBase
{
    [HttpGet(ApiRoutes.Stickers.Root)]
    public Task<StickerStateDto> GetState(CancellationToken ct) => stickers.GetStateAsync(ct);

    [HttpPut(ApiRoutes.Stickers.Installed)]
    public Task<StickerStateDto> Install(Guid packId, CancellationToken ct) => stickers.InstallAsync(packId, ct);

    [HttpDelete(ApiRoutes.Stickers.Installed)]
    public Task<StickerStateDto> Uninstall(Guid packId, CancellationToken ct) => stickers.UninstallAsync(packId, ct);

    [HttpPut(ApiRoutes.Stickers.InstalledOrder)]
    public Task<StickerStateDto> ReorderInstalled([FromBody] ReorderStickerPacksRequest request, CancellationToken ct) =>
        stickers.ReorderInstalledAsync(request, ct);

    [HttpPut(ApiRoutes.Stickers.Favorite)]
    public Task<StickerStateDto> AddFavorite(Guid stickerId, CancellationToken ct) => stickers.SetFavoriteAsync(stickerId, true, ct);

    [HttpDelete(ApiRoutes.Stickers.Favorite)]
    public Task<StickerStateDto> RemoveFavorite(Guid stickerId, CancellationToken ct) => stickers.SetFavoriteAsync(stickerId, false, ct);

    [HttpPost(ApiRoutes.Stickers.Gifs)]
    public Task<StickerStateDto> SaveGif([FromBody] SaveGifRequest request, CancellationToken ct) => stickers.SaveGifAsync(request, ct);

    [HttpDelete(ApiRoutes.Stickers.Gif)]
    public Task<StickerStateDto> RemoveGif(Guid mediaId, CancellationToken ct) => stickers.RemoveGifAsync(mediaId, ct);

    [HttpPost(ApiRoutes.Stickers.Packs)]
    public Task<StickerPackDto> CreatePack([FromBody] CreateStickerPackRequest request, CancellationToken ct) => packs.CreateAsync(request, ct);

    [HttpGet(ApiRoutes.Stickers.Pack)]
    public Task<StickerPackDto> GetPack(Guid packId, CancellationToken ct) => packs.GetAsync(packId, ct);

    [HttpGet(ApiRoutes.Stickers.PackBySlug)]
    public Task<StickerPackDto> GetPackBySlug(string slug, CancellationToken ct) => packs.GetBySlugAsync(slug, ct);

    [HttpPatch(ApiRoutes.Stickers.Pack)]
    public Task<StickerPackDto> RenamePack(Guid packId, [FromBody] UpdateStickerPackRequest request, CancellationToken ct) =>
        packs.RenameAsync(packId, request, ct);

    [HttpDelete(ApiRoutes.Stickers.Pack)]
    public async Task<IActionResult> DeletePack(Guid packId, CancellationToken ct)
    {
        await packs.DeleteAsync(packId, ct);
        return NoContent();
    }

    [HttpPost(ApiRoutes.Stickers.PackStickers)]
    public Task<StickerPackDto> AddSticker(Guid packId, [FromBody] AddStickerRequest request, CancellationToken ct) =>
        packs.AddStickerAsync(packId, request, ct);

    [HttpPatch(ApiRoutes.Stickers.PackSticker)]
    public Task<StickerPackDto> UpdateSticker(Guid packId, Guid stickerId, [FromBody] UpdateStickerRequest request, CancellationToken ct) =>
        packs.UpdateStickerAsync(packId, stickerId, request, ct);

    [HttpDelete(ApiRoutes.Stickers.PackSticker)]
    public Task<StickerPackDto> DeleteSticker(Guid packId, Guid stickerId, CancellationToken ct) =>
        packs.DeleteStickerAsync(packId, stickerId, ct);

    [HttpPut(ApiRoutes.Stickers.PackOrder)]
    public Task<StickerPackDto> ReorderStickers(Guid packId, [FromBody] ReorderStickersRequest request, CancellationToken ct) =>
        packs.ReorderStickersAsync(packId, request, ct);
}
