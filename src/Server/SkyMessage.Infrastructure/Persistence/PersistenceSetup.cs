using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkyMessage.Application.Common.Interfaces;
using SkyMessage.Application.Common.Options;

namespace SkyMessage.Infrastructure.Persistence;

public static class PersistenceSetup
{
    public const string ConnectionStringName = "Default";

    public static IServiceCollection AddPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException($"Connection string '{ConnectionStringName}' is not configured.");

        services.AddDbContext<AppDbContext>(options => Configure(options, connectionString));
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());
        return services;
    }

    internal static void Configure(DbContextOptionsBuilder options, string connectionString) =>
        options
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName))
            .UseSnakeCaseNamingConvention();

    /// <summary>
    /// When Server:MigrateOnStartup is true: applies migrations, or — while the project has no migrations
    /// yet (early development) — creates the schema from the model. After model changes in that phase,
    /// recreate the dev database (<c>docker compose down -v</c>). Migrations are owned by the lead only.
    /// </summary>
    public static async Task InitializeDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<ServerOptions>>().Value;
        if (!options.MigrateOnStartup) return;

        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(PersistenceSetup));
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (db.Database.GetMigrations().Any())
                    await db.Database.MigrateAsync(cancellationToken);
                else
                    await EnsureCreatedWithExtrasAsync(db, cancellationToken);
                logger.LogInformation("Database is up to date");
                return;
            }
            catch (Exception ex) when (attempt < 10 && ex is Npgsql.NpgsqlException or TimeoutException)
            {
                logger.LogWarning("Database not reachable yet (attempt {Attempt}): {Message}", attempt, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            }
        }
    }

    /// <summary>EnsureCreated plus the storage tweaks a migration would apply (also used by test fixtures).</summary>
    public static async Task EnsureCreatedWithExtrasAsync(AppDbContext db, CancellationToken cancellationToken = default)
    {
        if (await db.Database.EnsureCreatedAsync(cancellationToken))
            await db.Database.ExecuteSqlRawAsync(StorageTweaksSql, cancellationToken);
    }

    /// <summary>Media chunks are already compressed: store them out-of-line without TOAST compression.</summary>
    public const string StorageTweaksSql = "ALTER TABLE media_chunks ALTER COLUMN data SET STORAGE EXTERNAL;";
}

/// <summary>Lets <c>dotnet ef</c> create the context without starting the API.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("SKYMESSAGE_DESIGN_DB")
            ?? "Host=localhost;Port=5432;Database=skymessage;Username=skymessage;Password=skymessage";
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        PersistenceSetup.Configure(builder, connectionString);
        return new AppDbContext(builder.Options);
    }
}
