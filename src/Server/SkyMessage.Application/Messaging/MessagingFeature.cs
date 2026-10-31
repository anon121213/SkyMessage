using Microsoft.Extensions.DependencyInjection;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Application.Messaging.Services;

namespace SkyMessage.Application.Messaging;

/// <summary>Chats, groups, members, invites, messages, reactions, pins, read receipts, search.</summary>
public static class MessagingFeature
{
    public static IServiceCollection AddMessagingFeature(this IServiceCollection services)
    {
        services.AddScoped<MessageDtoLoader>();
        services.AddScoped<ChatSummaryBuilder>();
        services.AddScoped<MessagePageBuilder>();
        services.AddScoped<MentionResolver>();
        services.AddScoped<MessagingEvents>();
        services.AddScoped<ChatDeletion>();

        services.AddScoped<IChatService, ChatService>();
        services.AddScoped<IChatMemberService, ChatMemberService>();
        services.AddScoped<IMessageService, MessageService>();
        services.AddScoped<IMessageSearchService, MessageSearchService>();
        services.AddScoped<ISystemMessageService, SystemMessageService>();
        return services;
    }
}
