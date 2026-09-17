# Tower Browser

Tower Browser is a Windows x64 prototype that combines a native .NET 8/WPF
controller, an unpacked Mullvad Browser, and the pinned `custom-proxy-build`
Myst node from `myst-lmprove`. It routes only the bundled browser through a
loopback HTTP/CONNECT proxy; it does not change the Windows system proxy, DNS,
firewall, or route table.

## Project status and support

Version 1.0.10 is the normalized stable release line. Until its tag and release
are published, v1.0.9 remains the validated baseline. Versions v1.0.0-v1.0.8
are archived, unsupported test builds. See [SUPPORT.md](SUPPORT.md),
[CHANGELOG.md](CHANGELOG.md), and the
[GitHub releases page](https://github.com/25xr7yrs2y-oss/tower-browser/releases).

Tower Browser remains an unsigned prototype, not a claim of production
readiness. Windows CI builds and smoke-starts the WPF app and validates source,
policy, packaging, checksum, and selected security invariants. Clean-machine
standard-user, reboot, firewall-product, code-signing, fresh native packet
capture, and production payment validation remain incomplete. Read
[docs/VALIDATION_RESULTS.md](docs/VALIDATION_RESULTS.md) before relying on a
security or privacy claim.

## Supported platform

- Windows x64
- .NET 8 SDK for source builds; release packages are self-contained
- PowerShell 7 for the release tooling

Other operating systems are not supported runtime targets.

## Architecture

```text
TowerBrowser.exe (native WPF controller)
  -> starts the pinned myst.exe with its web UI disabled
  -> controls Myst through loopback TequilAPI at 127.0.0.1:44050

Mullvad Browser (isolated profile)
  -> locked HTTP/HTTPS proxy policy at 127.0.0.1:4449
  -> myst-lmprove userspace WireGuard netstack
  -> selected Mysterium provider
  -> Internet

all other Windows applications -> unchanged Windows network path
```

The browser has no direct-fallback proxy configuration. If the backend is not
ready or disappears, browser requests continue targeting the dead loopback
endpoint. DNS prefetch, speculative connections, DNS-over-HTTPS, and WebRTC are
locked off. Backend control, discovery, identity, payment, and provider traffic
is separate direct control-plane traffic and is inside the trust boundary.

See [docs/NATIVE_UI_ARCHITECTURE.md](docs/NATIVE_UI_ARCHITECTURE.md) and
[docs/IMPLEMENTATION_NOTES.md](docs/IMPLEMENTATION_NOTES.md).

## Download and run

Download the current portable ZIP and matching SHA-256 manifest from
[GitHub Releases](https://github.com/25xr7yrs2y-oss/tower-browser/releases).
Verify the ZIP against the published manifest, extract it to a user-writable
directory, and run `TowerBrowser.exe` from the extracted top-level folder.

The package is unsigned, so Windows may display an unknown-publisher warning.
Do not download release executables from mirrors. The archive checksum is the
current authenticity boundary; `bundle-manifest.json` detects missing or mixed
critical files after extraction but is not a code signature.

The upper-right **Controls** panel manages terms, identity, wallet balance,
top-up creation, provider discovery, connection, and isolated-browser launch.
Provider availability, identity registration, payment checkout, and wallet
credit depend on external Mysterium services.

## Build and run from source

Place unpacked runtime dependencies at:

```text
vendor/
  mullvad-browser/mullvadbrowser.exe
  myst-lmprove/resources/app.asar.unpacked/node_modules/@mysteriumnetwork/node/bin/win/x64/myst.exe
```

Then run:

```powershell
.\Build.ps1
.\Start-TowerBrowser.ps1
```

`Build.ps1` publishes the WPF application to `app\TowerBrowser.exe`.
`Start-PrivacyBrowser.ps1` remains only as a compatibility shim for older local
automation. `TOWER_BROWSER_ROOT` is the canonical development override;
`PRIVACY_BROWSER_ROOT` remains a compatibility fallback.

## Tests

On Windows with the .NET 8 SDK:

```powershell
.\Build.ps1
.\tests\Test-Configuration.ps1
.\tests\Test-Launcher.ps1
.\tests\Test-NativeArchitecture.ps1
.\tests\Test-NavigationArchitecture.ps1
.\tests\Test-BackendControls.ps1
.\tests\Test-PaymentSecurity.ps1
dotnet run --project .\tests\PrivacyBrowser.BackendController.Tests\PrivacyBrowser.BackendController.Tests.csproj --configuration Release
.\tests\Test-ProductHardening.ps1
.\tests\Test-ReleaseMetadata.ps1
.\tests\Test-Evidence.ps1
```

Packet-capture validation is a separate elevated workflow described in
[docs/VALIDATION_PLAN.md](docs/VALIDATION_PLAN.md).

## Security boundaries

- Tower Browser is not Tor and does not provide Tor circuits, relays, bridges,
  onion services, or Tor anonymity properties.
- HTTP CONNECT exposes destination hostnames to the local Myst backend.
- The local Myst control API is unauthenticated loopback HTTP and remains in the
  application trust boundary.
- Stripe, PayPal, and CoinGate orders are Mysterium/Pilvytis orders. Tower
  Browser has no merchant keys or credential-entry surface. No production
  payment success is claimed.
- Hosted checkout targets fail closed to documented HTTPS host/path contracts;
  checkout URLs and credentials are not persisted by the app.
- The portable build is not Authenticode-signed.

Report vulnerabilities according to [SECURITY.md](SECURITY.md). Do not include
sensitive vulnerability details in a public issue.

## Contributing

Read [CONTRIBUTING.md](CONTRIBUTING.md) before opening a pull request. General
usage questions and reproducible non-sensitive defects may use GitHub Issues;
the support boundaries are in [SUPPORT.md](SUPPORT.md).

## Compatibility identifiers

The canonical current product and executable names are **Tower Browser** and
`TowerBrowser.exe`. The internal `PrivacyBrowser.App` namespace, source/test
project paths, the single-instance mutex, legacy environment-variable fallback,
and compatibility launcher are intentionally retained in 1.0.10 to avoid a
high-risk namespace/storage migration. Historical v1.0.0-v1.0.9 release assets,
source-offer records, tags, and external links retain their original published
names.
