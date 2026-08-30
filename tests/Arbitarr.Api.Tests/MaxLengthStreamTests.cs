using Arbitarr.Api.Search;
using Xunit;

namespace Arbitarr.Api.Tests;

/// <summary>
/// SEC-L3: <see cref="MaxLengthStream"/> aborts a download once more than <see cref="MaxLengthStream.MaxBytes"/>
/// have been read, so a misbehaving/compromised upstream cannot exhaust memory/bandwidth via an
/// unbounded payload. Tested directly against the stream (via <see cref="Stream.ReadAsync(Memory{byte},CancellationToken)"/>)
/// in isolation from the download-proxy endpoint's HTTP plumbing; the endpoint reads through this
/// same stream into a bounded buffer before writing any response (see
/// <see cref="DownloadProxyTests"/> for coverage of that end-to-end 502 behavior).
/// </summary>
public class MaxLengthStreamTests
{
    /// <summary>
    /// A fake unbounded upstream stream: every <see cref="ReadAsync"/> reports having filled the
    /// whole requested buffer without touching it, so tests can simulate reading far past
    /// <see cref="MaxLengthStream.MaxBytes"/> without ever allocating/copying real payload bytes.
    /// </summary>
    private sealed class FakeUnboundedStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(buffer.Length);

        public override int Read(byte[] buffer, int offset, int count) => count;
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task ReadAsync_WithinBudget_DoesNotThrow()
    {
        await using var stream = new MaxLengthStream(new FakeUnboundedStream());
        var buffer = new byte[1024];

        var read = await stream.ReadAsync(buffer.AsMemory());

        Assert.Equal(1024, read);
    }

    [Fact]
    public async Task ReadAsync_OnceTotalExceedsMaxBytes_ThrowsDownloadTooLargeException()
    {
        await using var stream = new MaxLengthStream(new FakeUnboundedStream());
        var chunk = new byte[1024 * 1024]; // 1 MiB per read

        await Assert.ThrowsAsync<DownloadTooLargeException>(async () =>
        {
            // MaxBytes is 10 MiB; 11 reads of 1 MiB pushes the running total past it.
            // Each read's count is asserted rather than discarded (CA2022): the budget is only
            // genuinely exceeded if every read really did yield a full chunk, so a short read
            // would otherwise let this loop finish without ever crossing MaxBytes and leave the
            // ThrowsAsync assertion vacuous.
            for (var i = 0; i < 11; i++)
            {
                var read = await stream.ReadAsync(chunk.AsMemory());
                Assert.Equal(chunk.Length, read);
            }
        });
    }
}
