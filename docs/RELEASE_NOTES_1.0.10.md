# Tower Browser v1.0.10

Tower Browser 1.0.10 is the first stable release under the canonical product
name. It is a maintenance release based on the validated 1.0.9 code: no
browser-routing, payment, identity, provider, or backend functionality changed.

## Maintenance changes

- Normalizes current application UI, executable metadata, documentation,
  scripts, tests, CI artifacts, and future release assets to **Tower Browser**.
- Publishes the executable as `TowerBrowser.exe` and the portable archive as
  `TowerBrowser-1.0.10-windows-x64-portable.zip`.
- Keeps the legacy `PrivacyBrowser` source namespace, project directory, test
  project names, mutex, environment-variable fallback, and launcher shim for
  compatibility. Historical v1.0.0-v1.0.9 assets retain their published names.
- Generalizes release automation to strict `vX.Y.Z` tags, requires the tagged
  commit to be reachable from `main`, runs Windows validation before packaging,
  verifies expected artifacts and checksums, and refuses to overwrite a release.
- Adds project governance, contribution, support, security, and release-history
  documentation.

## Validation and security limitations

The Windows CI suite builds and smoke-starts the native WPF application and
checks policy, launcher, architecture, payment, metadata, evidence, bundle,
and checksum invariants. The package remains unsigned. Clean-machine
standard-user, reboot, firewall-product, and fresh native packet-capture
validation remain outstanding. Historical packet captures predate the native
UI and establish only the documented unchanged browser/proxy data-plane claims.

No live Stripe, PayPal, or CoinGate order was created. Production checkout,
payment completion, provider behavior, and MYST wallet credit are not claimed
as validated. See `docs/VALIDATION_RESULTS.md` and
`docs/PAYMENT_SECURITY_1.0.10.md`.
