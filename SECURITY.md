# Security Policy

## Supported Versions

| Version | Supported |
|---|---|
| Latest (`master`) | ✅ |
| Older releases | ❌ — please upgrade |

## Reporting a Vulnerability

If you discover a security vulnerability, **do not open a public issue**. Instead, please report it privately via GitHub's [Security Advisories](../../security/advisories/new) page (or email the maintainer directly if that is not available).

Please include:

- A description of the vulnerability
- Steps to reproduce
- Potential impact
- Any suggested mitigations

You will receive a response within **7 days**. We appreciate responsible disclosure.

## Security Notes for Users

- **Telegram Bot Token / Chat ID** are stored in plain text in the per-symbol settings JSON files inside the Quantower indicator output directory. Do not share these files publicly.
- The indicator does not transmit any data other than the Telegram chart screenshot when that feature is explicitly enabled.
- No external network connections are made unless **Enable Telegram Screenshot** is `true`.
