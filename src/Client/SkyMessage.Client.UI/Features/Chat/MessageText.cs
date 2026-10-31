using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using SkyMessage.Client.UI.Services;
using SkyMessage.Contracts;

namespace SkyMessage.Client.UI.Features.Chat;

/// <summary>
/// Message text with formatting entities, spoilers and safe links. The text is untrusted: it is emitted as text nodes
/// only (never markup), link targets are re-validated, and links open through <see cref="LinkService"/> (allow-list,
/// confirmation for named links) or in-app navigation for mentions. <see cref="ChildContent"/> is appended inline
/// after the last line (the bubble's time spacer).
/// </summary>
public sealed class MessageText : ComponentBase
{
    private IReadOnlyList<TextBlock> _blocks = [];
    private string? _laidOutText;
    private IReadOnlyList<MessageEntityDto>? _laidOutEntities;
    private readonly HashSet<int> _revealed = [];

    [Inject] private LinkService Links { get; set; } = default!;
    [Inject] private NavigationService Nav { get; set; } = default!;

    [Parameter] public string? Text { get; set; }
    [Parameter] public IReadOnlyList<MessageEntityDto>? Entities { get; set; }
    /// <summary>False renders links as plain text (selection mode).</summary>
    [Parameter] public bool Interactive { get; set; } = true;
    [Parameter] public string? Class { get; set; }
    [Parameter] public RenderFragment? ChildContent { get; set; }

    protected override void OnParametersSet()
    {
        if (ReferenceEquals(Text, _laidOutText) && ReferenceEquals(Entities, _laidOutEntities)) return;
        _laidOutText = Text;
        _laidOutEntities = Entities;
        _revealed.Clear();
        try
        {
            _blocks = MessageTextLayout.Build(Text, Entities);
        }
        catch (Exception)
        {
            // Malformed entities must never break the chat: fall back to the plain text.
            _blocks = string.IsNullOrEmpty(Text) ? [] : [new TextBlock(TextBlockKind.Plain, [new TextSpan(Text, TextStyle.None, TextLinkKind.None, null, null, 0)], null)];
        }
    }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "span");
        builder.AddAttribute(1, "class", string.IsNullOrEmpty(Class) ? "mtext" : "mtext " + Class);
        builder.AddAttribute(2, "dir", "auto");
        foreach (var block in _blocks)
        {
            switch (block.Kind)
            {
                case TextBlockKind.Pre:
                    builder.OpenElement(10, "pre");
                    builder.AddAttribute(11, "class", "mtext__pre");
                    if (block.Language is { } language) builder.AddAttribute(12, "data-lang", language);
                    // Long lines scroll sideways: neither a swipe back nor a swipe to reply starts here.
                    builder.AddAttribute(13, "data-no-swipe-back", "");
                    builder.OpenElement(14, "code");
                    builder.AddContent(15, block.Spans.Count > 0 ? block.Spans[0].Text : "");
                    builder.CloseElement();
                    builder.CloseElement();
                    break;
                case TextBlockKind.Quote:
                    builder.OpenElement(20, "span");
                    builder.AddAttribute(21, "class", "mtext__quote");
                    RenderSpans(builder, block.Spans);
                    builder.CloseElement();
                    break;
                default:
                    RenderSpans(builder, block.Spans);
                    break;
            }
        }
        builder.AddContent(30, ChildContent);
        builder.CloseElement();
    }

    private void RenderSpans(RenderTreeBuilder builder, IReadOnlyList<TextSpan> spans)
    {
        foreach (var span in spans)
        {
            builder.OpenRegion(40);
            if (span.Link != TextLinkKind.None && Interactive && !IsHiddenSpoiler(span))
            {
                builder.OpenElement(0, "a");
                builder.AddAttribute(1, "class", span.Link is TextLinkKind.Mention or TextLinkKind.Username ? "sm-mention" : "sm-link");
                builder.AddAttribute(2, "href", HrefOf(span));
                if (span.Link is TextLinkKind.Url or TextLinkKind.TextLink)
                {
                    builder.AddAttribute(3, "rel", "noopener noreferrer nofollow");
                    builder.AddAttribute(4, "target", "_blank");
                }
                builder.AddAttribute(5, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, () => OpenAsync(span)));
                builder.AddEventPreventDefaultAttribute(6, "onclick", true);
                builder.AddEventStopPropagationAttribute(7, "onclick", true);
                RenderStyled(builder, span, 8);
                builder.CloseElement();
            }
            else
            {
                RenderStyled(builder, span, 10);
            }
            builder.CloseRegion();
        }
    }

    private void RenderStyled(RenderTreeBuilder builder, TextSpan span, int sequence)
    {
        builder.OpenRegion(sequence);
        var opened = 0;
        if ((span.Style & TextStyle.Spoiler) != 0)
        {
            var revealed = _revealed.Contains(span.SpoilerGroup);
            builder.OpenElement(0, "span");
            builder.AddAttribute(1, "class", revealed ? "mtext__spoiler mtext__spoiler--open" : "mtext__spoiler");
            if (!revealed)
            {
                builder.AddAttribute(2, "role", "button");
                builder.AddAttribute(3, "onclick", EventCallback.Factory.Create<MouseEventArgs>(this, () => Reveal(span.SpoilerGroup)));
                builder.AddEventStopPropagationAttribute(4, "onclick", true);
            }
            opened++;
        }
        if ((span.Style & TextStyle.Bold) != 0) { builder.OpenElement(5, "strong"); opened++; }
        if ((span.Style & TextStyle.Italic) != 0) { builder.OpenElement(6, "em"); opened++; }
        if ((span.Style & TextStyle.Underline) != 0) { builder.OpenElement(7, "u"); opened++; }
        if ((span.Style & TextStyle.Strike) != 0) { builder.OpenElement(8, "s"); opened++; }
        if ((span.Style & TextStyle.Code) != 0)
        {
            builder.OpenElement(9, "code");
            builder.AddAttribute(10, "class", "mtext__code");
            opened++;
        }
        builder.AddContent(11, span.Text);
        for (var i = 0; i < opened; i++) builder.CloseElement();
        builder.CloseRegion();
    }

    private bool IsHiddenSpoiler(TextSpan span) => (span.Style & TextStyle.Spoiler) != 0 && !_revealed.Contains(span.SpoilerGroup);

    private void Reveal(int group)
    {
        _revealed.Add(group);
        StateHasChanged();
    }

    private static string HrefOf(TextSpan span) => span.Link switch
    {
        TextLinkKind.Mention => $"/user/{span.UserId}",
        TextLinkKind.Username => $"/u/{Uri.EscapeDataString(span.Target ?? "")}",
        _ => span.Target ?? "#",
    };

    private async Task OpenAsync(TextSpan span)
    {
        switch (span.Link)
        {
            case TextLinkKind.Mention when span.UserId is { } userId:
                Nav.OpenUser(userId);
                break;
            case TextLinkKind.Username when !string.IsNullOrEmpty(span.Target):
                Nav.OpenUsername(span.Target);
                break;
            case TextLinkKind.Url or TextLinkKind.Email or TextLinkKind.Phone or TextLinkKind.TextLink when span.Target is { } target:
                await Links.OpenAsync(target, span.Text);
                break;
        }
    }
}
