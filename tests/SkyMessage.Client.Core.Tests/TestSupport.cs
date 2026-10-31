using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SkyMessage.Client.Core.Infrastructure;
using SkyMessage.Client.Core.Platform;
using SkyMessage.Client.Core.Services;
using SkyMessage.Client.Core.Settings;
using SkyMessage.Contracts;

namespace SkyMessage.Client.Core.Tests;

internal static class Log
{
    public static ILogger<T> For<T>() => NullLogger<T>.Instance;
}

internal static class Wait
{
    /// <summary>Polls until <paramref name="condition"/> holds (async store work completes on other continuations).</summary>
    public static async Task UntilAsync(Func<bool> condition, string? because = null, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met in time" + (because is null ? "" : ": " + because));
            await Task.Delay(10);
        }
    }
}

/// <summary>A wall clock under test control (frozen unless advanced); timers, delays and timestamps stay real.</summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private long _ticks = start.UtcTicks;

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
}

/// <summary>Delays and timers fire at once (retry backoffs in tests).</summary>
internal sealed class InstantTime : TimeProvider
{
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new InstantTimer();
        if (dueTime != Timeout.InfiniteTimeSpan)
            ThreadPool.QueueUserWorkItem(_ =>
            {
                if (!timer.Disposed) callback(state);
            });
        return timer;
    }

    private sealed class InstantTimer : ITimer
    {
        public volatile bool Disposed;

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class InMemoryCacheStore : ILocalCacheStore
{
    public ConcurrentDictionary<string, string> Entries { get; } = new();

    /// <summary>The device storage as a crashed process left it.</summary>
    public InMemoryCacheStore Clone()
    {
        var copy = new InMemoryCacheStore();
        foreach (var (key, value) in Entries) copy.Entries[key] = value;
        return copy;
    }

    public Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(Entries.TryGetValue(key, out var value) ? value : null);

    public Task WriteAsync(string key, string content, CancellationToken cancellationToken = default)
    {
        Entries[key] = content;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        Entries.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task ClearAsync(string prefix = "", CancellationToken cancellationToken = default)
    {
        foreach (var key in Entries.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList()) Entries.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task<long> GetSizeBytesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Entries.Values.Sum(v => (long)v.Length * 2));
}

internal sealed class InMemorySecureStore : ISecureStore, IPreferencesStore
{
    public ConcurrentDictionary<string, string> Values { get; } = new();

    public Task<string?> GetAsync(string key) => Task.FromResult(Values.TryGetValue(key, out var value) ? value : null);

    public Task SetAsync(string key, string? value)
    {
        if (value is null) Values.TryRemove(key, out _);
        else Values[key] = value;
        return Task.CompletedTask;
    }

    Task ISecureStore.SetAsync(string key, string value) => SetAsync(key, value);

    public Task RemoveAsync(string key)
    {
        Values.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}

/// <summary>Named locks shared by every "instance" (tab/process) that uses the same object.</summary>
internal sealed class SharedLock : IInterProcessLock
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    public int Acquisitions;

    public async Task<IAsyncDisposable> AcquireAsync(string name, CancellationToken cancellationToken = default)
    {
        var gate = _locks.GetOrAdd(name, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        Interlocked.Increment(ref Acquisitions);
        return new Releaser(gate);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            gate.Release();
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class InMemoryOutboxFiles : IOutboxFileStore
{
    public ConcurrentDictionary<Guid, (string Name, string Type, byte[] Bytes)> Files { get; } = new();

    public InMemoryOutboxFiles Clone()
    {
        var copy = new InMemoryOutboxFiles();
        foreach (var (key, value) in Files) copy.Files[key] = value;
        return copy;
    }

    public bool IsSupported => true;

    public async Task<PickedFile> PersistAsync(Guid itemKey, PickedFile source, CancellationToken cancellationToken = default)
    {
        await using var stream = await source.OpenReadAsync(cancellationToken);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, cancellationToken);
        Files[itemKey] = (source.FileName, source.ContentType, copy.ToArray());
        return Open(itemKey)!;
    }

    public Task<PickedFile?> TryOpenAsync(Guid itemKey) => Task.FromResult(Open(itemKey));

    public Task DeleteAsync(Guid itemKey)
    {
        Files.TryRemove(itemKey, out _);
        return Task.CompletedTask;
    }

    public Task<long> GetSizeBytesAsync() => Task.FromResult(Files.Values.Sum(f => (long)f.Bytes.Length));

    private PickedFile? Open(Guid key)
    {
        if (!Files.TryGetValue(key, out var file)) return null;
        return new PickedFile
        {
            FileName = file.Name,
            ContentType = file.Type,
            Size = file.Bytes.Length,
            OpenReadAsync = _ => Task.FromResult<Stream>(new MemoryStream(file.Bytes, writable: false)),
        };
    }
}

/// <summary>HTTP stub: answers by delegate; throwing simulates network failures.</summary>
internal sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public ConcurrentQueue<Uri> Requests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Enqueue(request.RequestUri!);
        return respond(request);
    }

    public static HttpResponseMessage Json<T>(T value, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(value, SkyJson.Options), Encoding.UTF8, "application/json"),
    };

    public static HttpResponseMessage Bytes(int length) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[length]) };
}

internal static class Fakes
{
    public static ISettingsService Settings(AppSettings? settings = null)
    {
        var service = Substitute.For<ISettingsService>();
        service.Current.Returns(settings ?? new AppSettings());
        return service;
    }

    public static IMediaFileStore NoMediaFiles()
    {
        var files = Substitute.For<IMediaFileStore>();
        files.IsSupported.Returns(false);
        return files;
    }

    public static MediaCacheTrimmer Trimmer(IMediaFileStore? files = null, ISettingsService? settings = null, TimeProvider? time = null) =>
        new(files ?? NoMediaFiles(), settings ?? Settings(), time ?? TimeProvider.System, Log.For<MediaCacheTrimmer>());

    public static (LocalCache Cache, AccountLifecycle Lifecycle) Account(InMemoryCacheStore? store = null)
    {
        var cache = new LocalCache(store ?? new InMemoryCacheStore(), Log.For<LocalCache>());
        return (cache, new AccountLifecycle(cache, NoMediaFiles(), Log.For<AccountLifecycle>()));
    }

    public static IAuthService Auth(Guid userId, string username = "me_user")
    {
        var auth = Substitute.For<IAuthService>();
        auth.UserId.Returns(userId);
        auth.State.Returns(AuthState.SignedIn);
        auth.Me.Returns(new MeDto { Id = userId, Username = username, DisplayName = "Me" });
        return auth;
    }

    public static INetworkStatusService Network(bool connected = true)
    {
        var network = Substitute.For<INetworkStatusService>();
        network.IsConnected.Returns(connected);
        network.Kind.Returns(connected ? NetworkKind.Wifi : NetworkKind.None);
        return network;
    }

    public static MessageDto Message(Guid chatId, long seq, Guid? sender = null, string? text = null, DateTimeOffset? updatedAt = null,
        Guid? id = null, Guid? clientMessageId = null)
    {
        var created = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(seq);
        return new MessageDto
        {
            Id = id ?? Guid.CreateVersion7(),
            ChatId = chatId,
            Seq = seq,
            SenderId = sender ?? Guid.CreateVersion7(),
            Type = MessageType.Text,
            Text = text ?? $"message {seq}",
            CreatedAt = created,
            UpdatedAt = updatedAt ?? created,
            ClientMessageId = clientMessageId,
        };
    }

    public static MessagesPageDto Page(IEnumerable<MessageDto> messages, bool hasMoreBefore = false, bool hasMoreAfter = false, string cursor = "cursor-1") => new()
    {
        Messages = messages.OrderBy(m => m.Seq).ToList(),
        HasMoreBefore = hasMoreBefore,
        HasMoreAfter = hasMoreAfter,
        SyncCursor = cursor,
    };
}
