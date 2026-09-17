# Security Policy

## Supported versions

Arbitarr is in early development with no released versions yet. Security fixes land on `master`.

## Reporting a vulnerability

Please **do not open a public issue** for security vulnerabilities.

Instead, use [GitHub's private vulnerability reporting](https://github.com/JustAGameZA/arbitarr/security/advisories/new) for this repository. You should get an initial response within a week.

## Scope notes

Arbitarr is designed to run inside a private network, brokering between other self-hosted services (Sonarr/Radarr, NZBHydra2, Ollama). Reports we especially care about:

- Credential leakage: API keys appearing in logs, error messages, cached data, or fixture captures.
- Request forgery or injection through Torznab/Newznab query parameters.
- Anything that would cause the LLM arbitration layer to exfiltrate data from search results or metadata to somewhere it shouldn't go.

## Repository hygiene

No credentials or real network addresses are committed to this repository — fixtures are redacted and hosts use RFC 5737 documentation addresses. GitHub secret scanning and push protection are enabled. If you find a lapse in this (a real key or address in history), report it privately per the above.

## Known issues (fixed)

**Indexer key in console logs for redirect-mode sources.** Before commit `2723805` landed on `master`, a source configured for redirect NZB access mode caused the download route's redirect destination — the indexer's own link, API key included — to be written to console output at Information level on every download. The line came from the framework's redirect logging, and the console provider does not filter it out — console output is deliberately unfiltered and unscrubbed so the container's console view stays raw, and the log cleanser runs only on the way into the persistent store. Under Arbitarr's default logging configuration it did not reach the persistent log store (`/api/admin/logs`): that provider carries a category filter demoting everything under `Microsoft` to `Warning`, and the line was logged under a framework category below that threshold. That filter is configuration, so an operator who had raised verbosity for framework categories may have it in the store as well.

If you ran a source in redirect access mode and ship container console output to a log aggregator, file, or forwarder:

- Rotate that indexer's API key at the indexer itself, then update the new key in Arbitarr — that key authenticates your account to that indexer, so anyone holding it can search and download against your quota.
- Check your log aggregator, file, or forwarder for the old key and remove or expire those entries per its retention tooling.
- Check the persistent log store's retention (`/api/admin/logs`) as well: as above, whether the line reached it depends on your logging configuration.

The `V0.1` pre-release predates redirect access mode and is not affected. Only `master` builds taken between redirect access mode landing and `2723805` are. There is no GitHub Security Advisory for this — Arbitarr's [Scope notes](#scope-notes) already treats this class of leak as in-scope, and this note plus the fix is judged sufficient.
