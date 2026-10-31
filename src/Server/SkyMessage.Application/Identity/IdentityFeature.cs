using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SkyMessage.Application.Common.Interfaces;

namespace SkyMessage.Application.Identity;

/// <summary>Auth, sessions, users/profiles, privacy, contacts, blocks, user search, presence.</summary>
public static class IdentityFeature
{
    public static IServiceCollection AddIdentityFeature(this IServiceCollection services)
    {
        // Cross-feature services (Common interfaces).
        services.AddScoped<ISessionValidator, SessionValidator>();
        services.AddScoped<IPrivacyService, PrivacyService>();
        services.AddScoped<IUserSummaryService, UserSummaryService>();

        // Process-wide state.
        services.AddSingleton<LoginThrottle>();
        services.AddSingleton<ServerInstance>();

        // Use cases and helpers.
        services.AddScoped<ISessionSnapshotStore, DbSessionSnapshotStore>();
        services.AddScoped<SavedChatProvider>();
        services.AddScoped<SessionRevoker>();
        services.AddScoped<UserChangeNotifier>();
        services.AddScoped<AuthService>();
        services.AddScoped<AccountService>();
        services.AddScoped<SessionService>();
        services.AddScoped<ProfileService>();
        services.AddScoped<UserDirectoryService>();
        services.AddScoped<ContactService>();
        services.AddScoped<BlockService>();
        services.AddScoped<ServerInfoService>();
        services.AddScoped<RealtimeIdentityService>();
        services.AddScoped<PresenceBroadcaster>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IChatMembershipObserver, IdentityMembershipObserver>());

        return services;
    }
}
