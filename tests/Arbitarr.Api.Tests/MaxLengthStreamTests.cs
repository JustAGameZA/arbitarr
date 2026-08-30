using Arbitarr.Api.Search;
using Xunit;

namespace Arbitarr.Api.Tests;

/// <summary>
/// SEC-L3: <see cref="MaxLengthStream"/> aborts a download once more than <see cref="MaxLengthStream.MaxBytes"/>
/// have been read, so a misbehaving/compromised upstream cannot exhaust memory/bandwidth via an
/// unbounded payload. Tested directly against the stream (via <see cref="Stream.ReadAsync(Memory{byte},CancellationToken)"/>),
/// not through the full download-proxy endpoint: <c>Results.Stream</c> defers reading to the
/// ASP.NET Core response-writing pipeline, so <c>DownloadProxyEndpoint.HandleAsync</c>'s own catch
/// block never actually observes this exception in production — a known, documented limitation.
/// This test instead pins the one thing that's actually reachable and testable: the stream itself
/// throws when its budget is exceeded.
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
            for (var i = 0; i < 11; i++)
            {
                await stream.ReadAsync(chunk.AsMemory());
            }
        });
    }
}
