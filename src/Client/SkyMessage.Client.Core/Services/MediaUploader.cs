using Microsoft.Extensions.Logging;
using SkyMessage.Client.Core.Infrastructure;
using SkyMessage.Contracts;

namespace SkyMessage.Client.Core.Services;

/// <summary>
/// Chunked, resumable uploads (SPEC §8.1 IMediaUploader): chunks of the server's ChunkSize, at most 3 in flight,
/// 5 attempts per chunk with backoff, resume via GET upload (already received chunks are skipped), then complete
/// and wait for Ready (MediaProcessed event, polling every 1.5 s, at most 10 minutes). Progress: 0–0.95 while
/// uploading, 1 when the media is Ready.
/// </summary>
internal sealed class MediaUploader(ISkyApi api, IRealtimeService realtime, TimeProvider time, ILogger<MediaUploader> logger) : IMediaUploader
{
    internal const int MaxParallelChunks = 3;
    internal const int MaxAttempts = 5;
    internal static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1500);
    internal static readonly TimeSpan ProcessingTimeout = TimeSpan.FromMinutes(10);

    public Task<MediaDto> UploadAsync(MediaUploadRequest request, IProgress<double>? progress = null, CancellationToken cancellationToken = default) =>
        UploadAsync(request, resumeMediaId: null, onCreated: null, progress, cancellationToken);

    /// <param name="resumeMediaId">Upload session from an earlier attempt (outbox after a restart); a fresh one is created when it is gone.</param>
    /// <param name="onCreated">Called once the upload session exists, so callers can persist its id for resuming.</param>
    /// <param name="onSent">Called once the server has every byte, before the wait for its processing (up to minutes for a video).</param>
    internal async Task<MediaDto> UploadAsync(MediaUploadRequest request, Guid? resumeMediaId, Func<Guid, Task>? onCreated,
        IProgress<double>? progress, CancellationToken cancellationToken, Action? onSent = null)
    {
        if (request.Size <= 0) throw new ApiException(400, ErrorCodes.Validation, "The file is empty.");

        UploadSessionDto? session = null;
        if (resumeMediaId is { } existing)
        {
            try
            {
                session = await api.GetUploadAsync(existing, cancellationToken).ConfigureAwait(false);
                if (session.Status == MediaStatus.Failed) session = null;
            }
            catch (ApiException ex) when (ex.StatusCode == 404)
            {
                session = null;
            }
        }

        if (session is null)
        {
            session = await RetryAsync(() => api.CreateUploadAsync(new CreateUploadRequest
            {
                Kind = request.Kind,
                FileName = request.FileName,
                ContentType = request.ContentType,
                SizeBytes = request.Size,
                Width = request.Width,
                Height = request.Height,
                DurationMs = request.DurationMs,
                Waveform = request.Waveform,
            }, cancellationToken), cancellationToken).ConfigureAwait(false);
            if (onCreated is not null) await onCreated(session.MediaId).ConfigureAwait(false);
        }

        var mediaId = session.MediaId;
        // Subscribe before completing so a fast MediaProcessed cannot be missed.
        var processed = new TaskCompletionSource<MediaDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnProcessed(MediaDto media)
        {
            if (media.Id == mediaId && media.Status is MediaStatus.Ready or MediaStatus.Failed) processed.TrySetResult(media);
        }
        realtime.MediaProcessed += OnProcessed;
        try
        {
            if (session.Status == MediaStatus.Uploading)
                await UploadChunksAsync(request, session, progress, cancellationToken).ConfigureAwait(false);
            progress?.Report(0.95);

            var completed = await RetryAsync(() => api.CompleteUploadAsync(mediaId, cancellationToken), cancellationToken).ConfigureAwait(false);
            onSent?.Invoke();
            var ready = completed.Status is MediaStatus.Ready or MediaStatus.Failed
                ? completed
                : await WaitForProcessingAsync(mediaId, processed.Task, cancellationToken).ConfigureAwait(false);

            if (ready.Status == MediaStatus.Failed)
                throw new ApiException(422, ErrorCodes.MediaUnsupported, "The server could not process the file.");
            progress?.Report(1);
            return ready;
        }
        finally
        {
            realtime.MediaProcessed -= OnProcessed;
        }
    }

    private async Task UploadChunksAsync(MediaUploadRequest request, UploadSessionDto session, IProgress<double>? progress, CancellationToken ct)
    {
        var chunkSize = session.ChunkSize > 0 ? session.ChunkSize : Limits.DefaultChunkSize;
        var chunkCount = session.ChunkCount > 0 ? session.ChunkCount : (int)((request.Size + chunkSize - 1) / chunkSize);
        var received = session.ReceivedChunks.ToHashSet();
        long uploadedBytes = received.Sum(i => ChunkLength(request.Size, chunkSize, i));
        Report(progress, uploadedBytes, request.Size);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var inFlight = new List<Task>();
        var progressLock = new object();
        await using var stream = await request.OpenReadAsync(ct).ConfigureAwait(false);

        try
        {
            for (var index = 0; index < chunkCount; index++)
            {
                var length = ChunkLength(request.Size, chunkSize, index);
                if (received.Contains(index))
                {
                    await SkipAsync(stream, length, cts.Token).ConfigureAwait(false);
                    continue;
                }

                var buffer = new byte[length];
                var read = await stream.ReadAtLeastAsync(buffer, length, throwOnEndOfStream: false, cts.Token).ConfigureAwait(false);
                if (read < length)
                    throw new ApiException(400, CoreErrorCodes.AttachmentUnavailable, "The file is shorter than announced.");

                while (inFlight.Count >= MaxParallelChunks)
                {
                    var done = await Task.WhenAny(inFlight).ConfigureAwait(false);
                    inFlight.Remove(done);
                    await done.ConfigureAwait(false);
                }

                var chunkIndex = index;
                inFlight.Add(Task.Run(async () =>
                {
                    await UploadChunkWithRetryAsync(session.MediaId, chunkIndex, buffer, cts.Token).ConfigureAwait(false);
                    long total;
                    lock (progressLock) total = uploadedBytes += buffer.Length;
                    Report(progress, total, request.Size);
                }, CancellationToken.None));
            }
            await Task.WhenAll(inFlight).ConfigureAwait(false);
        }
        catch
        {
            await cts.CancelAsync().ConfigureAwait(false);
            try
            {
                await Task.WhenAll(inFlight).ConfigureAwait(false);
            }
            catch
            {
                // The first failure is the one reported.
            }
            throw;
        }
    }

    private async Task UploadChunkWithRetryAsync(Guid mediaId, int index, byte[] data, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await api.UploadChunkAsync(mediaId, index, data, ct).ConfigureAwait(false);
                return;
            }
            catch (ApiException ex) when (ex.StatusCode == 409 && ex.Code == ErrorCodes.ChunkInvalid)
            {
                // "Already complete": a previous attempt finished the upload.
                return;
            }
            catch (ApiException ex) when (ex.IsTransient && attempt < MaxAttempts && !ct.IsCancellationRequested)
            {
                logger.LogDebug("Chunk {Index} of {MediaId} failed ({Code}); retry {Attempt}", index, mediaId, ex.Code, attempt);
                await Task.Delay(Backoff(attempt), time, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<T> RetryAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (ApiException ex) when (ex.IsTransient && attempt < MaxAttempts && !ct.IsCancellationRequested)
            {
                await Task.Delay(Backoff(attempt), time, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<MediaDto> WaitForProcessingAsync(Guid mediaId, Task<MediaDto> processed, CancellationToken ct)
    {
        var deadline = time.GetTimestamp();
        while (true)
        {
            if (time.GetElapsedTime(deadline) > ProcessingTimeout)
                throw new ApiException(0, ClientErrorCodes.Timeout, "Media processing took too long.");

            using (var wait = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                var delay = Task.Delay(PollInterval, time, wait.Token);
                var first = await Task.WhenAny(processed, delay).ConfigureAwait(false);
                await wait.CancelAsync().ConfigureAwait(false);
                if (first == processed) return await processed.ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();

            try
            {
                var media = await api.GetMediaAsync(mediaId, ct).ConfigureAwait(false);
                if (media.Status is MediaStatus.Ready or MediaStatus.Failed) return media;
            }
            catch (ApiException ex) when (ex.IsTransient)
            {
                // Keep waiting: the event or a later poll will tell.
            }
        }
    }

    private static async Task SkipAsync(Stream stream, int length, CancellationToken ct)
    {
        if (stream.CanSeek)
        {
            stream.Seek(length, SeekOrigin.Current);
            return;
        }
        var scratch = new byte[Math.Min(length, 64 * 1024)];
        var remaining = length;
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(scratch.AsMemory(0, Math.Min(scratch.Length, remaining)), ct).ConfigureAwait(false);
            if (read == 0) throw new ApiException(400, CoreErrorCodes.AttachmentUnavailable, "The file is shorter than announced.");
            remaining -= read;
        }
    }

    internal static int ChunkLength(long size, int chunkSize, int index) =>
        (int)Math.Max(0, Math.Min(chunkSize, size - (long)index * chunkSize));

    internal static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(Math.Min(16, Math.Pow(2, attempt - 1)));

    private static void Report(IProgress<double>? progress, long uploaded, long size)
    {
        if (progress is null || size <= 0) return;
        progress.Report(Math.Clamp(uploaded / (double)size, 0, 1) * 0.95);
    }
}
