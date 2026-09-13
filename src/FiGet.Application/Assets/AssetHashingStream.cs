using System.Security.Cryptography;

namespace FiGet.Application.Assets;

/// <summary>The upload went past the configured size limit.</summary>
public sealed class AssetTooLargeException(long limit) : Exception($"The upload exceeds the limit of {limit} bytes.")
{
    public long Limit { get; } = limit;
}

/// <summary>What was learned about a file while it was stored.</summary>
public sealed record AssetHashes(long Size, string Md5, string Sha1, string Sha256, string Sha512);

/// <summary>
/// Reads through to an upload while hashing it and counting it, so a file of a gigabyte is hashed in the
/// same pass that stores it rather than read twice. The four hashes are the four the asset API reports.
///
/// MD5 and SHA-1 are here for compatibility, not for security: clients compare them against what they
/// downloaded, and nothing on this server trusts them. SHA-256 is what the ETag uses.
/// </summary>
internal sealed class AssetHashingStream(Stream inner, long maxBytes) : Stream
{
#pragma warning disable CA5351, CA5350 // Reported to clients as checksums; never used to decide trust.
    private readonly IncrementalHash md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
    private readonly IncrementalHash sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
#pragma warning restore CA5351, CA5350
    private readonly IncrementalHash sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly IncrementalHash sha512 = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
    private long size;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => size;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var read = inner.Read(buffer);
        Observe(buffer[..read]);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken);
        Observe(buffer.Span[..read]);
        return read;
    }

    public AssetHashes Finish() => new(
        size,
        Convert.ToHexStringLower(md5.GetHashAndReset()),
        Convert.ToHexStringLower(sha1.GetHashAndReset()),
        Convert.ToHexStringLower(sha256.GetHashAndReset()),
        Convert.ToHexStringLower(sha512.GetHashAndReset()));

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            md5.Dispose();
            sha1.Dispose();
            sha256.Dispose();
            sha512.Dispose();
        }

        base.Dispose(disposing);
    }

    private void Observe(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        size += data.Length;
        if (size > maxBytes)
        {
            throw new AssetTooLargeException(maxBytes);
        }

        md5.AppendData(data);
        sha1.AppendData(data);
        sha256.AppendData(data);
        sha512.AppendData(data);
    }
}
