namespace Arbitarr.Api.Search;

/// <summary>
/// SEC-L3: wraps an upstream download stream and throws <see cref="DownloadTooLargeException"/>
/// as soon as more than <see cref="MaxBytes"/> have been read, so a misbehaving or compromised
/// upstream cannot exhaust Arbitarr's memory/bandwidth by streaming an unbounded payload through
/// the download proxy. The underlying stream is read incrementally (never buffered whole), so the
/// limit is enforced without ever holding the full payload in memory.
/// </summary>
public sealed class MaxLengthStream : Stream
{
    /// <summary>Maximum number of bytes this stream will pass through before aborting.</summary>
    public const long MaxBytes = 10 * 1024 * 1024;

    private readonly Stream _inner;
    private long _totalRead;

    public MaxLengthStream(Stream inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        _totalRead += read;
        if (_totalRead > MaxBytes)
        {
            throw new DownloadTooLargeException();
        }

        return read;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => throw new NotSupportedException();
    public override int Read(Span<byte> buffer) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>Thrown by <see cref="MaxLengthStream"/> when the wrapped stream exceeds the allowed size.</summary>
public sealed class DownloadTooLargeException : Exception
{
    public DownloadTooLargeException()
        : base($"Download exceeded the maximum allowed size of {MaxLengthStream.MaxBytes} bytes.")
    {
    }
}
