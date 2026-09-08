using System.Collections.Concurrent;

namespace Arbitarr.Api.Security;

/// <summary>
/// #44: the brute-force posture for the login route — a fixed-window counter, per username AND per
/// remote address (plan §4 step 9, which requires the chosen behaviour to be STATED rather than
/// merely present).
///
/// <para><b>THE NUMBERS, AND WHY THESE.</b> Within a fifteen-minute window:</para>
/// <list type="bullet">
/// <item><b>5 failures per username.</b> Bounds an online guessing attack against one account to
/// ~20 attempts an hour, which defeats guessing at any realistic password strength while leaving an
/// operator who mistyped twice unaffected.</item>
/// <item><b>20 failures per remote address.</b> Bounds an attacker spraying one common password
/// across many usernames — the attack the per-username limit does not see at all, because each
/// username accrues only one failure. Set well above the per-username limit so a single operator
/// fumbling one password never trips it.</item>
/// </list>
///
/// <para><b>ONLY FAILURES COUNT, AND A SUCCESS CLEARS.</b> A successful login resets that username's
/// counter, so an operator who eventually remembers their password is not left locked out by the
/// attempts that preceded it. The address counter is not cleared by a success, because the address
/// is not the thing that authenticated.</para>
///
/// <para><b>THIS IS A LOCKOUT-FREE DESIGN, DELIBERATELY.</b> Exceeding a limit delays (429), it does
/// not disable the account. A persistent lockout on the only account of an appliance with NO
/// password recovery (see <c>UserEntry</c>) would hand any attacker who can reach the port a
/// permanent denial of service against the operator — the attacker needs no credential to trigger
/// it, only the ability to fail repeatedly. A window that drains on its own is the right trade for
/// a single-operator homelab box.</para>
///
/// <para><b>IN-MEMORY, AND THEREFORE RESET BY A RESTART.</b> Not persisted, because a persisted
/// counter would be a write per failed login — an unauthenticated caller controlling a database
/// write — which trades one denial-of-service surface for a worse one. A restart clearing the
/// counters is a real limitation and an acceptable one: an attacker cannot induce the restart, and
/// the operator who can has better options than guessing their own password.</para>
///
/// <para>Singleton, because the counters must outlive a request; the state is a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> touched from many request threads at once.</para>
/// </summary>
public sealed class LoginRateLimiter
{
    /// <summary>Failures allowed per username per <see cref="Window"/> before a 429.</summary>
    public const int MaxFailuresPerUsername = 5;

    /// <summary>Failures allowed per remote address per <see cref="Window"/> before a 429.</summary>
    public const int MaxFailuresPerAddress = 20;

    /// <summary>
    /// The fixed window. Fifteen minutes: long enough that exhausting the per-username budget makes
    /// guessing hopeless, short enough that an operator who genuinely locks themselves out is
    /// waiting through one cup of tea rather than abandoning the evening.
    /// </summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, Counter> _counters = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    public LoginRateLimiter(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>
    /// Whether this attempt is allowed through to credential verification. Called BEFORE the
    /// password is checked, so a throttled attempt costs no KDF work — otherwise the rate limit
    /// would bound guessing but not the CPU exhaustion that unbounded KDF invocations allow.
    ///
    /// <para><b><paramref name="addressScope"/> SEPARATES THE ADDRESS BUDGET, AND OMITTING IT IS
    /// LOGIN'S BEHAVIOUR.</b> A distinct flow that shares this limiter must pass its own scope, or
    /// its failures spend the LOGIN budget of the same address — see <see cref="AddressKey"/>.</para>
    /// </summary>
    public bool IsAllowed(string? username, string? remoteAddress, string? addressScope = null)
    {
        var now = _timeProvider.GetUtcNow();

        return Count(UsernameKey(username), now) < MaxFailuresPerUsername
            && Count(AddressKey(remoteAddress, addressScope), now) < MaxFailuresPerAddress;
    }

    /// <summary>
    /// Records a failed attempt against both the username and the address budgets.
    /// <paramref name="addressScope"/> must match the one passed to <see cref="IsAllowed"/>, or the
    /// counter this increments is not the one that is read.
    /// </summary>
    public void RecordFailure(string? username, string? remoteAddress, string? addressScope = null)
    {
        var now = _timeProvider.GetUtcNow();
        Increment(UsernameKey(username), now);
        Increment(AddressKey(remoteAddress, addressScope), now);
        PruneExpired(now);
    }

    /// <summary>
    /// Drops counters whose window has ended.
    ///
    /// <para><b>THIS IS NOT TIDINESS — IT IS WHY THE MAP CANNOT BE GROWN ON PURPOSE.</b> The keys
    /// here are partly ATTACKER-SUPPLIED: a username comes straight off an unauthenticated request
    /// body, so without pruning, an attacker posting a million distinct usernames would pin a
    /// million counters in memory and turn this defence into the memory-exhaustion vector it exists
    /// to prevent. An expired counter is indistinguishable from an absent one to every read
    /// (<see cref="Count"/> already treats it as zero), so removing it changes no behaviour.</para>
    ///
    /// <para>Swept on failure rather than on a timer: failures are the only calls that ADD entries,
    /// so the sweep runs exactly when growth happens and never on an idle instance. The cost is
    /// bounded by the number of distinct keys seen in one window, and the removal is conditional on
    /// the value being unchanged, so a concurrent increment is never discarded.</para>
    /// </summary>
    private void PruneExpired(DateTimeOffset now)
    {
        foreach (var (key, counter) in _counters)
        {
            if (now >= counter.WindowEndsAt)
            {
                ((ICollection<KeyValuePair<string, Counter>>)_counters)
                    .Remove(new KeyValuePair<string, Counter>(key, counter));
            }
        }
    }

    /// <summary>
    /// Clears the username's budget after a successful login. The ADDRESS budget is deliberately
    /// left alone: one success from an address that has failed nineteen times against nineteen
    /// different usernames is what a successful spray looks like, and forgiving it would let the
    /// attacker reset their own limit by finally guessing right.
    /// </summary>
    public void RecordSuccess(string? username)
    {
        _counters.TryRemove(UsernameKey(username), out _);
    }

    private int Count(string key, DateTimeOffset now)
    {
        if (!_counters.TryGetValue(key, out var counter) || now >= counter.WindowEndsAt)
        {
            return 0;
        }

        return counter.Failures;
    }

    private void Increment(string key, DateTimeOffset now)
    {
        _counters.AddOrUpdate(
            key,
            _ => new Counter(1, now.Add(Window)),
            // An expired window is REPLACED rather than extended, which is what makes this a fixed
            // window rather than a sliding one: the budget refills completely once the window ends,
            // instead of the window creeping forward on every attempt and eventually locking an
            // account out permanently under a slow, persistent attacker.
            (_, existing) => now >= existing.WindowEndsAt
                ? new Counter(1, now.Add(Window))
                : existing with { Failures = existing.Failures + 1 });
    }

    // Namespaced so a username can never collide with an address — without the prefixes, an account
    // literally named after an IP address would share a budget with that address.
    private static string UsernameKey(string? username) =>
        $"u:{(username ?? string.Empty).Trim().ToLowerInvariant()}";

    /// <summary>
    /// The address budget's key. <paramref name="addressScope"/> is null for login, which keeps
    /// login's key exactly <c>a:{address}</c> — the shape it has always had.
    ///
    /// <para><b>A FLOW THAT DOES NOT PASS A SCOPE SHARES LOGIN'S ADDRESS BUDGET.</b> The username
    /// side namespaces itself by whatever the caller passes (#96 uses <c>pw:{userId}</c>), but the
    /// address side cannot: the address is the same string either way. So without a scope, five
    /// failed password changes would spend five of the twenty per-address LOGIN failures, and an
    /// operator behind a shared NAT egress could be pushed into a 429 on the sign-in page by their
    /// own rotation attempts. Two attacks want two budgets on BOTH axes, not just one.</para>
    /// </summary>
    private static string AddressKey(string? remoteAddress, string? addressScope) =>
        addressScope is null
            ? $"a:{remoteAddress ?? "unknown"}"
            : $"a:{addressScope}:{remoteAddress ?? "unknown"}";

    private sealed record Counter(int Failures, DateTimeOffset WindowEndsAt);
}
