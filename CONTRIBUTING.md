# Contributing to Tower Browser

Tower Browser accepts focused bug fixes, tests, documentation improvements, and
carefully scoped security hardening through GitHub pull requests.

## Before opening a change

1. Search existing issues and pull requests.
2. For non-trivial behavior changes, open an issue describing the user problem,
   Windows impact, and validation approach before implementation.
3. Never include credentials, identity keys, payment data, private URLs, or
   sensitive vulnerability details in an issue, commit, log, or fixture.

## Development requirements

- Windows x64 and the .NET 8 SDK are required for the full test suite.
- Preserve the browser-scoped, fail-closed proxy model and the documented trust
  boundaries.
- Keep dependencies pinned and document their origin, version, hash, license,
  and corresponding source obligations.
- Do not weaken validation or security caveats to make a build pass.
- Preserve historical tags, release assets, and versioned source-offer records.

Run the commands in the README's **Tests** section. Pull requests must pass the
`Windows tests / powershell-tests` check. Changes to packaging or dependencies
also require updates to the matching versioned dependency, source-offer,
payment-security, release-note, and checksum expectations.

Use a focused branch and explain the change, risk, compatibility effect, and
actual validation in the pull request template. By participating, you agree to
follow [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md).
