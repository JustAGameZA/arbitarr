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

**Indexer key in console logs for redirect-mode sources.** Before the fix landed on `master`, a source configured for redirect NZB access mode caused the download route's redirect destination — the indexer's own link, API key included — to be written to console output at Information level on every download. The line came from the framework's redirect logging, and the console provider does not filter it out. It never reached the persistent log store (`/api/admin/logs`): that store's sink applies a higher level floor to the category the line was logged under, so store-reading operators would not have seen it there.

If you ran a source in redirect access mode and ship container console output to a log aggregator, file, or forwarder:

- Rotate that indexer's API key at the indexer itself, then update the new key in Arbitarr.
- Check your log aggregator, file, or forwarder for the old key and remove or expire those entries per its retention tooling.
- Check the persistent log store's retention (`/api/admin/logs`) as well, even though this specific line should not have reached it.

No released version is affected, since there are no released versions yet; the fix is on `master`. There is no GitHub Security Advisory for this — Arbitarr's [Scope notes](#scope-notes) already treats this class of leak as in-scope, and this note plus the fix is judged sufficient for a pre-release project.
