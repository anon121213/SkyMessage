using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using SkyMessage.Contracts;
using Xunit;

namespace SkyMessage.Api.IntegrationTests.Security;

/// <summary>
/// SPEC §1.4 / §3: "tokens never in logs; message text never logged; structured, ids only". A host with a capturing
/// logger (every SkyMessage category down to Trace, framework categories at their configured levels) runs a
/// realistic session; none of the secrets may appear in any log line, structured value or exception.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class LoggingHygieneTests(ApiFactory factory)
{
    [Fact]
    public async Task Secrets_and_message_text_never_reach_the_logs()
    {
        var sink = new CapturingLoggerProvider();
        await using var host = factory.WithWebHostBuilder(builder => builder.ConfigureLogging(logging =>
        {
            logging.AddProvider(sink);
            logging.AddFilter<CapturingLoggerProvider>("SkyMessage", LogLevel.Trace);
        }));
        var secrets = new List<string>();
        string Secret(string prefix)
        {
            var value = SecurityKit.Unique(prefix).Replace(' ', '-');
            secrets.Add(value);
            return value;
        }

        var password = Secret("pw");
        var alice = await RegisterAsync(host, password);
        var bob = await RegisterAsync(host, Secret("pw"));
        secrets.AddRange([alice.Auth.AccessToken, alice.Auth.RefreshToken, alice.Auth.MediaKey, bob.Auth.AccessToken, bob.Auth.RefreshToken]);

        // Failed and successful sign-ins, a refresh, a password change.
        var anonymous = Client(host);
        using (await anonymous.PostAsJsonAsync(ApiRoutes.Auth.Login,
                   new LoginRequest { Login = alice.Username, Password = Secret("wrong"), Device = SecurityKit.Device("x") }, SkyJson.Options, SecurityKit.Ct)) { }
        var second = await anonymous.PostJsonAsync<AuthResponse>(ApiRoutes.Auth.Login,
            new LoginRequest { Login = alice.Username, Password = password, Device = SecurityKit.Device("x") });
        var refreshed = await anonymous.PostJsonAsync<AuthResponse>(ApiRoutes.Auth.Refresh, new RefreshRequest { RefreshToken = second.RefreshToken });
        secrets.AddRange([second.AccessToken, second.RefreshToken, second.MediaKey, refreshed.AccessToken, refreshed.RefreshToken]);

        // Realtime with typing, messages with links, an edit, a search, a deletion.
        await using var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(host.Server.BaseAddress, HubRoutes.Realtime.TrimStart('/')), o =>
            {
                o.HttpMessageHandlerFactory = _ => host.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
                o.AccessTokenProvider = () => Task.FromResult<string?>(bob.Auth.AccessToken);
            })
            .AddJsonProtocol(o => SkyJson.Configure(o.PayloadSerializerOptions))
            .Build();
        await hub.StartAsync(SecurityKit.Ct);

        var chat = await alice.Client.PostJsonAsync<ChatSummaryDto>(ApiRoutes.Chats.Private, new CreatePrivateChatRequest { UserId = bob.Id });
        var text = Secret("text");
        var link = "https://example.org/" + Secret("path");
        var message = await alice.Client.PostJsonAsync<MessageDto>(ApiRoutes.Messages.RootPath(chat.Id), new SendMessageRequest
        {
            ClientMessageId = Guid.NewGuid(),
            Text = text,
            Entities = [new MessageEntityDto { Type = MessageEntityType.TextLink, Offset = 0, Length = 4, Url = link }],
        });
        await hub.InvokeAsync(nameof(IRealtimeServer.SendTyping), chat.Id, TypingAction.Typing, SecurityKit.Ct);
        var edited = Secret("edited");
        await alice.Client.PatchJsonAsync<MessageDto>(ApiRoutes.Messages.ByIdPath(chat.Id, message.Id), new EditMessageRequest { Text = edited });
        await bob.Client.GetJsonAsync<MessagesPageDto>(ApiRoutes.Messages.SearchPath(chat.Id, edited));
        using (await alice.Client.PostAsJsonAsync(ApiRoutes.Messages.RootPath(chat.Id),
                   new SendMessageRequest { ClientMessageId = Guid.NewGuid(), Text = Secret("too-long") + new string('x', Limits.MessageTextMaxLength) },
                   SkyJson.Options, SecurityKit.Ct)) { }
        await alice.Client.NoContentAsync(HttpMethod.Post, ApiRoutes.Messages.DeletePath(chat.Id), new DeleteMessagesRequest { MessageIds = [message.Id], ForEveryone = true });

        // A file with a telling name and content.
        var fileName = Secret("diagnosis") + ".txt";
        var content = System.Text.Encoding.UTF8.GetBytes(Secret("content"));
        var upload = await alice.Client.PostJsonAsync<UploadSessionDto>(ApiRoutes.Media.Uploads,
            new CreateUploadRequest { Kind = MediaKind.File, FileName = fileName, ContentType = "text/plain", SizeBytes = content.Length });
        using (var chunk = new ByteArrayContent(content))
        {
            chunk.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var put = await alice.Client.PutAsync(ApiRoutes.Media.UploadChunkPath(upload.MediaId, 0), chunk, SecurityKit.Ct);
            await put.ShouldHaveStatusAsync(System.Net.HttpStatusCode.NoContent);
        }
        using (await alice.Client.PostAsync(ApiRoutes.Media.UploadCompletePath(upload.MediaId), null, SecurityKit.Ct)) { }

        var newPassword = Secret("pw");
        await alice.Client.NoContentAsync(HttpMethod.Post, ApiRoutes.Auth.ChangePassword,
            new ChangePasswordRequest { CurrentPassword = password, NewPassword = newPassword, TerminateOtherSessions = true });
        await hub.StopAsync(SecurityKit.Ct);

        var lines = sink.Lines.ToList();
        // The capture works: the identity log lines (ids only) of this very session are there.
        lines.ShouldContain(line => line.Contains("SkyMessage.Application.Identity.AuthService", StringComparison.Ordinal)
                                    && line.Contains(alice.Id.ToString(), StringComparison.Ordinal));
        lines.ShouldContain(line => line.StartsWith("Debug SkyMessage.", StringComparison.Ordinal), "Debug-level SkyMessage logs are captured too");
        var leaks = secrets
            .SelectMany(secret => lines.Where(line => line.Contains(secret, StringComparison.Ordinal)).Select(line => $"[{SecurityKit.Truncate(secret, 24)}] {SecurityKit.Truncate(line, 300)}"))
            .ToList();
        leaks.ShouldBeEmpty();
    }

    private static HttpClient Client(WebApplicationFactory<Program> host)
    {
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.TryAddWithoutValidation("X-Forwarded-For", SecurityKit.NextIp());
        return client;
    }

    private static async Task<(HttpClient Client, AuthResponse Auth, string Username, Guid Id)> RegisterAsync(WebApplicationFactory<Program> host, string password)
    {
        var client = Client(host);
        var username = SecurityKit.NewUsername("logs");
        var auth = await client.PostJsonAsync<AuthResponse>(ApiRoutes.Auth.Register, new RegisterRequest
        {
            Username = username, Password = password, DisplayName = "Log Tester", Device = SecurityKit.Device("x"),
        });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return (client, auth, username, auth.User.Id);
    }

    /// <summary>Records the formatted message, every structured value and the exception of each log entry.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public IReadOnlyCollection<string> Lines => _lines;

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _lines);

        public void Dispose()
        {
        }

        private sealed class Logger(string category, ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var line = $"{logLevel} {category}: {formatter(state, exception)}";
                if (state is IEnumerable<KeyValuePair<string, object?>> values)
                    line += " | " + string.Join(", ", values.Select(v => $"{v.Key}={v.Value}"));
                if (exception is not null)
                    line += " | " + exception;
                lines.Enqueue(line);
            }
        }
    }
}
