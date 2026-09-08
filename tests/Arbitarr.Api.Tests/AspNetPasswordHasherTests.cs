using Arbitarr.Api.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Xunit;

namespace Arbitarr.Api.Tests;

/// <summary>
/// #44: the password KDF, and specifically the property that makes its cost adjustable —
/// a hash written at an OLDER iteration count must keep verifying.
///
/// <para>That is the whole reason the cost could be raised from the library's 100,000 default to
/// <see cref="AspNetPasswordHasher.IterationCount"/> without a migration, and it is the kind of
/// claim that is comfortable to assert in a comment and never check. If it were false, raising the
/// cost would lock out every existing operator on an appliance that has no password recovery — so
/// it is asserted here against a hash genuinely produced at the old cost, not a simulated one.</para>
/// </summary>
public sealed class AspNetPasswordHasherTests
{
    private const string Password = "example-operator-passphrase";

    /// <summary>
    /// Produces a hash the way a PREVIOUS release of Arbitarr would have: the same primitive at the
    /// library's old default cost. Built from <see cref="PasswordHasherOptions"/> rather than
    /// pasted as a literal so it stays a real v3 hash of <see cref="Password"/> — a hardcoded
    /// string would rot silently into a value nothing can verify, and the test would then pass by
    /// asserting nothing.
    /// </summary>
    private static string HashAtLegacyCost(string password)
    {
        var legacy = new PasswordHasher<object>(
            Options.Create(new PasswordHasherOptions { IterationCount = 100_000 }));

        return legacy.HashPassword(new object(), password);
    }

    [Fact]
    public void The_configured_cost_is_above_the_library_default()
    {
        // The point of the change: inheriting the default would leave it at 100,000.
        Assert.True(
            AspNetPasswordHasher.IterationCount > 100_000,
            $"Expected an explicitly raised iteration count, got {AspNetPasswordHasher.IterationCount}.");
    }

    [Fact]
    public void A_hash_written_at_the_old_iteration_count_still_verifies()
    {
        var hasher = new AspNetPasswordHasher();
        var legacyHash = HashAtLegacyCost(Password);

        // POSITIVE CONTROL FIRST: the legacy hash is a real, distinct artefact of THIS password —
        // not the password itself, and not the hash the current cost would produce. Without this,
        // "it verifies" could pass against a hasher that returned true for anything.
        Assert.NotEqual(Password, legacyHash);
        Assert.NotEqual(hasher.Hash(Password), legacyHash);

        Assert.True(
            hasher.Verify(legacyHash, Password),
            "A hash produced at the old iteration count must still verify, or raising the cost " +
            "would lock out every existing operator — and there is no password recovery.");
    }

    /// <summary>
    /// The legacy hash really does take the <see cref="PasswordVerificationResult.SuccessRehashNeeded"/>
    /// branch, asserted against the underlying library rather than inferred.
    ///
    /// <para><b>WHY THIS IS SEPARATE FROM THE TEST ABOVE.</b> That one asserts only that
    /// <see cref="AspNetPasswordHasher.Verify"/> returns <c>true</c>, which it would do just as
    /// happily if the library reported plain <see cref="PasswordVerificationResult.Success"/> —
    /// so it does NOT prove the rehash-needed branch is the one being exercised. If the two costs
    /// ever coincided (someone "tidying" <see cref="AspNetPasswordHasher.IterationCount"/> back to
    /// the default, say) that test would keep passing while the branch it exists to protect went
    /// untested. This one pins the library's actual verdict, so the branch is exercised rather than
    /// merely present.</para>
    /// </summary>
    [Fact]
    public void An_old_hash_reports_rehash_needed_while_a_current_one_does_not()
    {
        var library = new PasswordHasher<object>(
            Options.Create(new PasswordHasherOptions
            {
                IterationCount = AspNetPasswordHasher.IterationCount,
            }));

        var subject = new object();

        Assert.Equal(
            PasswordVerificationResult.SuccessRehashNeeded,
            library.VerifyHashedPassword(subject, HashAtLegacyCost(Password), Password));

        // The contrast that makes the assertion above meaningful: at the CURRENT cost the same
        // library reports plain Success, so "rehash needed" is a real signal about the old cost and
        // not something this hasher reports for everything.
        Assert.Equal(
            PasswordVerificationResult.Success,
            library.VerifyHashedPassword(subject, library.HashPassword(subject, Password), Password));
    }

    /// <summary>
    /// The DI-supplied options are honoured — the cost is a composition-root decision, not a
    /// literal baked into the class.
    /// </summary>
    [Fact]
    public void The_iteration_count_is_taken_from_the_injected_options()
    {
        // A deliberately cheap cost, which is the point: it is observable precisely because it is
        // NOT the class's own default, so this fails if the constructor ignores what it is given.
        var hasher = new AspNetPasswordHasher(
            Options.Create(new PasswordHasherOptions { IterationCount = 1_000 }));

        var hash = hasher.Hash(Password);

        // Round-trips under the injected cost...
        Assert.True(hasher.Verify(hash, Password));

        // ...and the library agrees the stored cost is the injected one, not IterationCount:
        // verifying it against a hasher configured at the higher cost reports rehash-needed.
        var atCurrentCost = new PasswordHasher<object>(
            Options.Create(new PasswordHasherOptions
            {
                IterationCount = AspNetPasswordHasher.IterationCount,
            }));

        Assert.Equal(
            PasswordVerificationResult.SuccessRehashNeeded,
            atCurrentCost.VerifyHashedPassword(new object(), hash, Password));
    }

    [Fact]
    public void A_wrong_password_still_fails_against_an_old_hash()
    {
        // The other half of the control. Accepting an old hash must not mean accepting anything
        // presented alongside one: a Verify that answered true unconditionally would satisfy the
        // test above and be a total authentication bypass.
        var hasher = new AspNetPasswordHasher();
        var legacyHash = HashAtLegacyCost(Password);

        Assert.False(hasher.Verify(legacyHash, "not-the-right-example-passphrase"));
    }

    [Fact]
    public void A_hash_at_the_current_cost_round_trips()
    {
        var hasher = new AspNetPasswordHasher();
        var hash = hasher.Hash(Password);

        Assert.NotEqual(Password, hash);
        Assert.True(hasher.Verify(hash, Password));
        Assert.False(hasher.Verify(hash, "not-the-right-example-passphrase"));
    }

    [Fact]
    public void Two_hashes_of_the_same_password_differ()
    {
        // Per-password salt, asserted rather than assumed: identical outputs would mean an
        // unsalted scheme, where one precomputed table breaks every account at once.
        var hasher = new AspNetPasswordHasher();

        Assert.NotEqual(hasher.Hash(Password), hasher.Hash(Password));
    }

    [Fact]
    public void A_corrupt_stored_hash_denies_access_rather_than_throwing()
    {
        // A row written by hand or damaged in the file must fail the login, not turn every attempt
        // into a 500 — the contract IPasswordHasher.Verify states.
        var hasher = new AspNetPasswordHasher();

        Assert.False(hasher.Verify("not-a-valid-hash", Password));
        Assert.False(hasher.Verify(string.Empty, Password));
    }
}
