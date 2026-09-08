namespace Arbitarr.Data.Security;

/// <summary>
/// #44: a rejected account operation, translated to a 400 by the auth endpoints — the same
/// validate-at-the-repository-boundary posture <see cref="ApiKeyValidationException"/> carries
/// (AC24: reject malformed input, never clamp it).
///
/// <para>The messages this carries are shown to an UNAUTHENTICATED caller, because the only route
/// that raises it is first-run setup. They therefore describe the rule that was broken (a length, a
/// missing field) and never the state of the system — "an account already exists" is deliberately
/// not one of them, because it would answer the one question an unauthenticated prober most wants
/// answered. See <see cref="UserRepository.CreateFirstUserAsync"/>.</para>
/// </summary>
public sealed class UserValidationException : Exception
{
    public UserValidationException(string message)
        : base(message)
    {
    }
}
