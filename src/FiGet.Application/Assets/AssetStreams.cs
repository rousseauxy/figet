namespace FiGet.Application.Assets;

/// <summary>
/// Reads through to another stream while counting, and stops at a limit. Used where bytes are only passed
/// along - a multipart part, an archive entry - and hashing them would be wasted work.
/// </summary>
internal sealed class CountingStream(Stream inner, long maxBytes) : Stream
{
    public long Count { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => Count;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Observe(inner.Read(buffer, offset, count));

    public override int Read(Span<byte> buffer) => Observe(inner.Read(buffer));

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Observe(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Observe(await inner.ReadAsync(buffer, cancellationToken));

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private int Observe(int read)
    {
        Count += read;
        if (Count > maxBytes)
        {
            throw new AssetTooLargeException(maxBytes);
        }

        return read;
    }
}

/// <summary>
/// The parts of a multipart upload read back as one stream, each opened only when the one before it is done,
/// so a file of many gigabytes never has more than one part file open.
/// </summary>
internal sealed class ConcatenatedStream(IReadOnlyList<Func<CancellationToken, Task<Stream>>> parts) : Stream
{
    private int next;
    private Stream? current;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            if (current is null)
            {
                if (next >= parts.Count)
                {
                    return 0;
                }

                current = await parts[next++](cancellationToken);
            }

            var read = await current.ReadAsync(buffer, cancellationToken);
            if (read > 0)
            {
                return read;
            }

            await current.DisposeAsync();
            current = null;
        }
    }

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
            current?.Dispose();
            current = null;
        }

        base.Dispose(disposing);
    }
}
