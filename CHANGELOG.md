# Changelog

## 1.0.10

- Adopted **Tower Browser** as the canonical current product name across UI,
  executable metadata, documentation, scripts, tests, and future artifacts.
- Generalized strict-SemVer release automation with main-ancestry, Windows test,
  artifact, checksum, and no-overwrite gates.
- Added repository governance, contribution, security, and support policies.
- Retained legacy internal identifiers and historical asset names where changing
  them would break compatibility or falsify release history.

## 1.0.9

- Replaced and reproducibly generated the WPF, executable, shortcut, and shell
  icon assets. This is the validated baseline preceding 1.0.10.

## 1.0.8

- Added native Mysterium-hosted Stripe and PayPal top-up flows, fail-closed
  checkout validation, and payment lifecycle reconciliation.

## 1.0.7

- Added application identity and portable-bundle integrity hardening.

## 1.0.6

- Switched packaging to the pinned raw Myst node and excluded the upstream
  installer, Electron shell, and supervisor.

## 1.0.5

- Added bounded backend operations, safe connection reconciliation, and related
  validation coverage.

## 1.0.4

- Incorporated security and validation refinements documented in its release
  notes.

## 1.0.3

- Hardened identity lifecycle, privacy readiness, and payment-target handling.

## 1.0.2

- Improved native backend controls, wallet workflow, and Windows QA packaging.

## 1.0.1

- Fixed WPF startup icon decoding on Windows.

## 1.0.0

- Initial public Windows x64 test build.

Versions 1.0.0-1.0.8 are archived unsupported test builds. Historical details
remain in `docs/RELEASE_NOTES_*.md` and their immutable GitHub releases.
