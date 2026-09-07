namespace Arbitarr.Data.Entities;

/// <summary>
/// An operator's judgement of one pipeline decision (#54 AC2), stored as
/// <see cref="EventEntry.ReviewVerdict"/> on the <see cref="EventKind.Decision"/> row it judges.
///
/// TWO MEMBERS, AND NO "UNREVIEWED" ONE. Not-yet-reviewed is represented by the column being NULL,
/// not by an enum member: a third member would make "unreviewed" a value the aggregate has to
/// remember to exclude from both halves of its ratio, and forgetting that is exactly how an
/// agreement rate silently starts counting the un-judged majority as disagreement. Nullability
/// makes the distinction unforgettable instead — a value is either a judgement or it is absent.
///
/// REVIEWING CHANGES NO PIPELINE BEHAVIOUR (plan §3.2, AC5). These verdicts are recorded and
/// aggregated so a human can decide whether to leave shadow mode; nothing feeds them back into rule
/// tuning or the AI layer. That is a v1 boundary held on purpose — making every review a mutation of
/// pipeline behaviour is a far larger safety surface, and it would make the queue's own accuracy
/// statistics circular. If feedback-driven tuning is wanted later it is its own issue.
/// </summary>
public enum ReviewVerdict
{
    /// <summary>The operator agrees the pipeline decided correctly.</summary>
    Agree = 0,

    /// <summary>The operator disagrees: the pipeline got this one wrong.</summary>
    Disagree = 1,
}
