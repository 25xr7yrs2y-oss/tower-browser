# Version 1.0.9 bundled dependencies

The portable package is assembled without running either upstream installer.
The 1.0.9 icon-only application update reuses the exact pinned and verified
backend binary shipped by 1.0.6 and the controller-side Stripe/PayPal behavior
shipped by 1.0.8. No Stripe or PayPal merchant SDK/library is added; the
existing Myst node and Mysterium Pilvytis service remain the order and
wallet-credit authority.

## Mullvad Browser

- Version: 15.0.14, Windows x86-64
- Official release: <https://github.com/mullvad/mullvad-browser/releases/tag/15.0.14>
- Installer SHA-256: `56d5e332b1e780c6413c1a88e7b0a855ec1df5a400a26d92f08585637bc75c02`
- The browser application tree is extracted and placed under `vendor/mullvad-browser`.

## myst-lmprove node

- Source commit: `7944a4c634834aac10a4e8e49934e326ac3f0e7a`
- Backend tag: `privacy-browser-backend-v1.0.6`
- Release: <https://github.com/25xr7yrs2y-oss/myst-lmprove/releases/tag/privacy-browser-backend-v1.0.6>
- Pinned asset: `myst-windows-x64.exe` (asset ID `537461102`, 52,600,832 bytes)
- Binary SHA-256: `5b761c82022d77bd1229ebb9e5e7bc35353a7e3c6b842e33967a643d181c25b2`
- Trusted build: <https://github.com/25xr7yrs2y-oss/myst-lmprove/actions/runs/33357639395>
- Embedded version: `privacy-browser-backend-v1.0.6`
- Embedded commit: `7944a4c634834aac10a4e8e49934e326ac3f0e7a`

The trusted backend workflow built and tested the Windows x64 node, executed
`myst.exe --version`, verified that its output contains the exact source commit
and release identifier, and published the binary with its version output and
JSON SHA-256 provenance record. Only this raw `myst.exe` is copied into the
portable package; the Electron shell, installer, and supervisor are not used.

## .NET runtime

The Windows x64 .NET 8 desktop runtime is included through self-contained publishing.
