# Security policy

## Supported versions

| Version | Status |
|---|---|
| 1.0.10 | Current supported stable release |
| 1.0.9 | Historical validated baseline; unsupported |
| 1.0.0-1.0.8 | Unsupported historical test builds |

Security fixes are made on the current supported line. Historical releases and
assets remain available for auditability but do not receive fixes.

## Reporting a vulnerability

GitHub private vulnerability reporting is not currently enabled for this
repository. Do not publish vulnerability details, exploit code, identity keys,
payment data, or other sensitive material in a public issue.

As the safe public fallback, open a minimal GitHub issue titled **Security
contact requested** that contains no vulnerability details. A repository
maintainer can then arrange an appropriate private channel. If no private
channel is offered, do not disclose sensitive details publicly.

For non-sensitive hardening suggestions or already-public dependency notices,
use a normal issue and link the public advisory.

## Scope and limitations

Tower Browser is unsigned prototype software. Review the README and
`docs/VALIDATION_RESULTS.md` for the exact validation scope and unresolved
limitations. Production payment success, provider behavior, clean-machine
standard-user operation, reboot persistence, firewall-product compatibility,
and fresh native packet-capture behavior are not claimed as validated.
