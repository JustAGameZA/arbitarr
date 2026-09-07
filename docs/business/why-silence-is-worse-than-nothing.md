# Why a wrong match costs more than a missing one

This matters because it is the value judgement the entire design rests on, and it is not the
default assumption in this ecosystem. Most tooling optimises for the grab: return something,
because an empty result feels like a failure. Arbitarr inverts that, and nearly every rule that
looks over-strict follows from this one position.

## The asymmetry

A **missing match** is visible and cheap. The episode does not arrive. The operator notices,
searches again, and adjusts. The cost is a delay, and the system's state remains truthful.

A **wrong match** is invisible and expensive. The wrong file downloads successfully, fills the
slot, is marked as satisfied, and stops the search. Nothing retries, because as far as the *arr
app knows the episode is present. It is discovered by a human watching the wrong episode, possibly
weeks later, and then has to be tracked back through a chain that recorded no error anywhere.

The costs are not symmetrical, so the thresholds should not be either.

## The same asymmetry in degradation

The point generalises past matching. When a metadata source is down or has no coverage, most
tooling degrades silently — it returns fewer or worse results and reports success. The operator
cannot distinguish "there is genuinely nothing" from "the thing that would have found it was
broken."

So every degraded path records *what* degraded, distinguishably: a cache miss is not an outage, an
outage is not a coverage gap, and a coverage gap is not a mapping collision. Each has a different
remediation. Collapsing them into one "degraded" state destroys the information the operator would
act on, and the collapse is always tempting because the code is simpler.

## What follows

- Ambiguous mappings admit **no** match, flagged as ambiguous, rather than picking a candidate.
  A tiebreak on genuinely ambiguous input is not a decision — it is a coin flip wearing a
  confidence score.
- Every match carries provenance: which source resolved the identity, what evidence supported it,
  and what was degraded. This is what makes a wrong match traceable after the fact instead of
  merely regrettable.
- Degradation flags are additive and independent, never collapsed.

## The tradeoff, stated plainly

Arbitarr returns nothing in cases where other tools return something. Some of those somethings
would have been correct.

That is the intended cost, accepted deliberately. It should not be "fixed" by adding a fallback
guess, and a PR that improves the hit rate by guessing is a regression regardless of what the
numbers say — because the metric that improved is the one that was never the problem.
