using System.Buffers;
using System.Data;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
using SkyMessage.Application.MediaFiles;
using SkyMessage.Contracts;
using SkyMessage.Infrastructure.Persistence;

namespace SkyMessage.Infrastructure.MediaFiles.Storage;

/// <summary>
/// Media bytes as <c>media_chunks</c> rows (bytea, storage EXTERNAL). Encrypted media use
/// <see cref="MediaChunkCipher"/>: <c>data</c> = nonce|ciphertext|tag, <c>size</c> = plaintext length.
/// </summary>
internal sealed class PostgresMediaChunkStore(AppDbContext db, MediaEncryption encryption, IServiceScopeFactory scopeFactory) : IMediaChunkStore
{
    // FOR SHARE conflicts with the row lock taken by "complete" (UPDATE ... SET status), so a chunk can never be
    // written after the upload was completed and the chunk count verified.
    private const string UpsertUploadChunkSql = """
        WITH target AS (
            SELECT id FROM media
            WHERE id = @media_id AND owner_id = @owner_id AND status = @uploading
            FOR SHARE
        )
        INSERT INTO media_chunks (media_id, "index", data, size)
        SELECT id, @index, @data, @size FROM target
        ON CONFLICT (media_id, "index") DO UPDATE SET data = EXCLUDED.data, size = EXCLUDED.size
        """;

    private const string InsertChunkSql = """
        INSERT INTO media_chunks (media_id, "index", data, size) VALUES (@media_id, @index, @data, @size)
        """;

    public byte[]? CreateWrappedKey(Guid mediaId) => encryption.CreateWrappedKey(mediaId);

    public async Task<bool> WriteUploadChunkAsync(Guid mediaId, Guid ownerId, byte[]? wrappedKey, int index, ReadOnlyMemory<byte> plaintext,
        CancellationToken cancellationToken = default)
    {
        using var cipher = encryption.CreateCipher(mediaId, wrappedKey);
        byte[]? rented = null;
        try
        {
            var data = plaintext;
            if (cipher is not null)
            {
                var length = MediaChunkCipher.EncryptedLength(plaintext.Length);
                rented = ArrayPool<byte>.Shared.Rent(length);
                cipher.Encrypt(index, plaintext.Span, rented.AsSpan(0, length));
                data = rented.AsMemory(0, length);
            }

            var rows = await db.Database.ExecuteSqlRawAsync(UpsertUploadChunkSql,
            [
                new NpgsqlParameter<Guid>("media_id", mediaId),
                new NpgsqlParameter<Guid>("owner_id", ownerId),
                new NpgsqlParameter<int>("uploading", (int)MediaStatus.Uploading),
                new NpgsqlParameter<int>("index", index),
                new NpgsqlParameter<ReadOnlyMemory<byte>>("data", data) { NpgsqlDbType = NpgsqlDbType.Bytea },
                new NpgsqlParameter<int>("size", plaintext.Length),
            ], cancellationToken);
            return rows > 0;
        }
        finally
        {
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public Stream OpenRead(MediaStorageInfo media)
    {
        if (ChunkMath.ChunkCount(media.SizeBytes, media.ChunkSize) != media.ChunkCount)
            throw new InvalidDataException($"Media {media.MediaId} has inconsistent chunk metadata.");
        var source = new PostgresChunkSource(scopeFactory, encryption, media.MediaId, media.WrappedKey, media.ChunkSize);
        return new ChunkedMediaStream(source, media.SizeBytes, media.ChunkSize);
    }

    public byte[] ReadThumbnail(Guid mediaId, byte[]? wrappedKey, byte[] storedData)
    {
        using var cipher = encryption.CreateCipher(mediaId, wrappedKey);
        return cipher is null ? storedData : cipher.Decrypt(MediaChunkCipher.ThumbnailIndex, storedData);
    }

    /// <summary>Thumbnail bytes as they are stored: encrypted with the media's cipher (index −1) when it has one.</summary>
    internal static byte[] ProtectThumbnail(MediaChunkCipher? cipher, byte[] plaintext) =>
        cipher is null ? plaintext : cipher.Encrypt(MediaChunkCipher.ThumbnailIndex, plaintext);

    /// <summary>
    /// Writes <paramref name="content"/> as chunks 0..n of <paramref name="mediaId"/> inside the caller's open
    /// transaction (old chunks must already be deleted). Returns the plaintext size, the chunk count and the
    /// SHA-256 (lower-case hex) of what was written.
    /// </summary>
    internal static async Task<StoredContent> WriteContentAsync(AppDbContext db, Guid mediaId, MediaChunkCipher? cipher, Stream content,
        int chunkSize, CancellationToken cancellationToken)
    {
        var transaction = db.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction
                          ?? throw new InvalidOperationException("Writing media content requires an open transaction.");
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();

        await using var command = new NpgsqlCommand(InsertChunkSql, connection, transaction);
        var indexParameter = new NpgsqlParameter<int>("index", 0);
        var dataParameter = new NpgsqlParameter<ReadOnlyMemory<byte>>("data", ReadOnlyMemory<byte>.Empty) { NpgsqlDbType = NpgsqlDbType.Bytea };
        var sizeParameter = new NpgsqlParameter<int>("size", 0);
        command.Parameters.Add(new NpgsqlParameter<Guid>("media_id", mediaId));
        command.Parameters.Add(indexParameter);
        command.Parameters.Add(dataParameter);
        command.Parameters.Add(sizeParameter);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var plain = ArrayPool<byte>.Shared.Rent(chunkSize);
        var sealedBuffer = cipher is null ? null : ArrayPool<byte>.Shared.Rent(MediaChunkCipher.EncryptedLength(chunkSize));
        try
        {
            long total = 0;
            var index = 0;
            while (true)
            {
                var read = await content.ReadAtLeastAsync(plain.AsMemory(0, chunkSize), chunkSize, throwOnEndOfStream: false, cancellationToken);
                if (read == 0) break;

                hash.AppendData(plain, 0, read);
                ReadOnlyMemory<byte> data = plain.AsMemory(0, read);
                if (cipher is not null)
                {
                    var length = MediaChunkCipher.EncryptedLength(read);
                    cipher.Encrypt(index, plain.AsSpan(0, read), sealedBuffer.AsSpan(0, length));
                    data = sealedBuffer.AsMemory(0, length);
                }

                indexParameter.TypedValue = index;
                dataParameter.TypedValue = data;
                sizeParameter.TypedValue = read;
                await command.ExecuteNonQueryAsync(cancellationToken);

                total += read;
                index++;
                if (read < chunkSize) break;
            }

            return new StoredContent(total, index, Convert.ToHexStringLower(hash.GetHashAndReset()));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(plain);
            if (sealedBuffer is not null) ArrayPool<byte>.Shared.Return(sealedBuffer);
        }
    }
}

internal sealed record StoredContent(long SizeBytes, int ChunkCount, string Sha256);

/// <summary>
/// Loads single chunks with plain ADO.NET straight into pooled buffers (no per-chunk byte[] allocations). Uses
/// its own DI scope so a response stream does not depend on the request's DbContext, and opens the pooled
/// connection only for the duration of each chunk read.
/// </summary>
internal sealed class PostgresChunkSource(
    IServiceScopeFactory scopeFactory,
    MediaEncryption encryption,
    Guid mediaId,
    byte[]? wrappedKey,
    int chunkSize) : IMediaChunkSource
{
    private const string SelectChunkSql = """
        SELECT octet_length(data), data FROM media_chunks WHERE media_id = @media_id AND "index" = @index
        """;

    private AsyncServiceScope? _scope;
    private AppDbContext? _db;
    private MediaChunkCipher? _cipher;
    private bool _cipherCreated;
    private byte[]? _payload;

    public async ValueTask<int> ReadChunkAsync(int index, Memory<byte> destination, CancellationToken cancellationToken)
    {
        var db = Context();
        var cipher = Cipher();
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = CreateCommand(db, index);
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess | CommandBehavior.SingleRow, cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidDataException($"Chunk {index} of media {mediaId} is missing.");

            var length = reader.GetInt32(0);
            var target = cipher is null ? destination : Payload().AsMemory();
            if (length > target.Length)
                throw new InvalidDataException($"Chunk {index} of media {mediaId} is larger than expected.");
            await using (var column = reader.GetStream(1))
                await column.ReadExactlyAsync(target[..length], cancellationToken);

            return cipher is null ? length : cipher.Decrypt(index, target.Span[..length], destination.Span);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    public int ReadChunk(int index, Span<byte> destination)
    {
        var db = Context();
        var cipher = Cipher();
        db.Database.OpenConnection();
        try
        {
            using var command = CreateCommand(db, index);
            using var reader = command.ExecuteReader(CommandBehavior.SequentialAccess | CommandBehavior.SingleRow);
            if (!reader.Read())
                throw new InvalidDataException($"Chunk {index} of media {mediaId} is missing.");

            var length = reader.GetInt32(0);
            var target = cipher is null ? destination : Payload().AsSpan();
            if (length > target.Length)
                throw new InvalidDataException($"Chunk {index} of media {mediaId} is larger than expected.");
            using (var column = reader.GetStream(1))
                column.ReadExactly(target[..length]);

            return cipher is null ? length : cipher.Decrypt(index, target[..length], destination);
        }
        finally
        {
            db.Database.CloseConnection();
        }
    }

    public void Dispose()
    {
        ReleaseBuffers();
        _scope?.Dispose();
        _scope = null;
    }

    public async ValueTask DisposeAsync()
    {
        ReleaseBuffers();
        if (_scope is { } scope)
        {
            _scope = null;
            await scope.DisposeAsync();
        }
    }

    private AppDbContext Context()
    {
        if (_db is not null) return _db;
        var scope = scopeFactory.CreateAsyncScope();
        _scope = scope;
        return _db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    }

    private MediaChunkCipher? Cipher()
    {
        if (_cipherCreated) return _cipher;
        _cipher = encryption.CreateCipher(mediaId, wrappedKey);
        _cipherCreated = true;
        return _cipher;
    }

    private byte[] Payload() => _payload ??= ArrayPool<byte>.Shared.Rent(MediaChunkCipher.EncryptedLength(chunkSize));

    private NpgsqlCommand CreateCommand(AppDbContext db, int index)
    {
        var command = new NpgsqlCommand(SelectChunkSql, (NpgsqlConnection)db.Database.GetDbConnection());
        command.Parameters.Add(new NpgsqlParameter<Guid>("media_id", mediaId));
        command.Parameters.Add(new NpgsqlParameter<int>("index", index));
        return command;
    }

    private void ReleaseBuffers()
    {
        _cipher?.Dispose();
        _cipher = null;
        if (_payload is not null)
        {
            ArrayPool<byte>.Shared.Return(_payload);
            _payload = null;
        }
    }
}
