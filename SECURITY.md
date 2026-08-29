# Security Policy

## Supported versions

Arbitarr is in early development with no released versions yet. Security fixes land on `master`.

## Reporting a vulnerability

Please **do not open a public issue** for security vulnerabilities.

Instead, use [GitHub's private vulnerability reporting](https://github.com/ZerithZA/arbitarr/security/advisories/new) for this repository. You should get an initial response within a week.

## Scope notes

Arbitarr is designed to run inside a private network, brokering between other self-hosted services (Sonarr/Radarr, NZBHydra2, Ollama). Reports we especially care about:

- Credential leakage: API keys appearing in logs, error messages, cached data, or fixture captures.
- Request forgery or injection through Torznab/Newznab query parameters.
- Anything that would cause the LLM arbitration layer to exfiltrate data from search results or metadata to somewhere it shouldn't go.

## Repository hygiene

No credentials or real network addresses are committed to this repository — fixtures are redacted and hosts use RFC 5737 documentation addresses. GitHub secret scanning and push protection are enabled. If you find a lapse in this (a real key or address in history), report it privately per the above.
