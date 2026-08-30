using System.Security.Cryptography;
using Arbitarr.Api.Rendering;
using Arbitarr.Core.Releases;
using Xunit;

namespace Arbitarr.Api.Tests;

/// <summary>
/// SEC-L2: <see cref="ReleaseGuid"/> is keyed by a per-instance HMAC secret so proxy guids cannot
/// be predicted/enumerated without knowing it. These tests pin <see cref="ReleaseGuid.Configure"/>'s
/// secret-swap effect directly: same input under one configured secret always produces the same
/// guid, and reconfiguring with a different secret changes the guid for that same input.
///
/// <see cref="ReleaseGuid"/>'s secret is process-wide static state (by design — see its class
/// remarks), so this test restores a fresh random secret in a finally block to avoid leaking a
/// known/fixed secret into other tests that run in the same process.
/// </summary>
[Collection("ReleaseGuidSecret")]
public class ReleaseGuidSecretSwapTests
{
    [Fact]
    public void Configure_WithSameSecret_ProducesStableGuidForSameInput()
    {
        var hmacKey = RandomNumberGenerator.GetBytes(32);
        var identity = new ReleaseIdentity("eztv", "123");

        try
        {
            ReleaseGuid.Configure(hmacKey);
            var first = ReleaseGuid.Compute(identity);
            var second = ReleaseGuid.Compute(identity);

            Assert.Equal(first, second);
        }
        finally
        {
            ReleaseGuid.Configure(RandomNumberGenerator.GetBytes(32));
        }
    }

    [Fact]
    public void Configure_WithDifferentSecret_ProducesDifferentGuidForSameInput()
    {
        var identity = new ReleaseIdentity("eztv", "123");

        try
        {
            ReleaseGuid.Configure(RandomNumberGenerator.GetBytes(32));
            var beforeSwap = ReleaseGuid.Compute(identity);

            ReleaseGuid.Configure(RandomNumberGenerator.GetBytes(32));
            var afterSwap = ReleaseGuid.Compute(identity);

            Assert.NotEqual(beforeSwap, afterSwap);
        }
        finally
        {
            ReleaseGuid.Configure(RandomNumberGenerator.GetBytes(32));
        }
    }
}

/// <summary>
/// Marker collection so tests mutating <see cref="ReleaseGuid"/>'s shared static secret never run
/// concurrently with each other or with other tests that depend on a stable guid (e.g.
/// <c>ReleaseGuidStabilityTests</c>), per xUnit's default same-collection-runs-sequentially rule.
/// </summary>
[CollectionDefinition("ReleaseGuidSecret", DisableParallelization = true)]
public class ReleaseGuidSecretCollection
{
}
