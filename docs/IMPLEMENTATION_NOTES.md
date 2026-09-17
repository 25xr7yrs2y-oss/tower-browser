# Implementation Notes

## Base selection

Mullvad Browser is the base because it already removes normal Tor-network
operation while retaining Tor Browser-derived anti-fingerprinting hardening.
The prototype uses an upstream unpacked Windows build and adds only a scoped
enterprise policy, isolated profile, backend lifecycle, and validation tools.
This avoids maintaining a full Firefox fork before the routing architecture is
proven.

Exact source checked: branch `mullvad-browser-140.12.0esr-15.0-1`, commit
`ba34b2b615afc4dff62f5b9db4be8f04e32f2602`. Source findings:

- `001-base-profile.js` enables RFP, canvas randomization, letterboxing,
  first-party isolation, disabled telemetry, disabled predictor/prefetch/DNS
  prefetch, and defense-in-depth WebRTC ICE restrictions.
- `000-mullvad-browser.js` enables Mullvad DoH with `network.trr.mode=3`; this
  prototype deliberately overrides it with locked mode 5 because a browser DoH
  connection must not bypass the local HTTP proxy model.
- Mullvad updater URLs and automatic-update defaults remain in source; the
  prototype's `DisableAppUpdate` policy prevents an unvalidated version from
  replacing the tested build.
- Enterprise policy source and `test_proxy.js` confirm manual HTTP/SSL address,
  all-protocol HTTP proxy, passthrough, and locked proxy behavior used here.
- No Tor daemon/control-port source path was present in the selected sparse
  runtime areas; Tor issue references remain in inherited hardening comments.

## Backend source audit

Audited branch: `25xr7yrs2y-oss/myst-lmprove@7944a4c`, branch
`custom-proxy-build`.

- Previous desktop entry: Electron `src/main/index.tsx` started a loopback web
  server and Myst node. The prototype no longer invokes that executable during
  normal operation; its native WPF controller starts the bundled `myst.exe`
  directly with `--ui.enable=false`.
- Node entry: `src/main/node/mysteriumNode.ts` launches `myst.exe` with
  `--usermode --proxymode --proxy.bind.address=127.0.0.1 --consumer`.
- Protocol: `custom-node/services/wireguard/endpoint/proxyclient/handler.go`
  implements ordinary HTTP forwarding and HTTP CONNECT. No SOCKS server was
  found in the proxyclient implementation despite the README saying SOCKS/HTTP.
- Bind: proxyclient constructs `127.0.0.1:4449` from the explicit bind flag and
  connection option.
- Tunnel model: proxy mode selects `proxyclient.New()`, which uses WireGuard's
  userspace netstack (`CreateNetTUN`) and an HTTP server. It does not select the
  Wintun/kernel path.
- System routing: proxy mode skips peer route exclusion at the P2P call site and
  selects a no-op routing manager, so it does not discover or mutate host routes
  or access the supervisor. Actual system-tunnel modes keep route protection;
  initial gateway discovery is bounded, backed off, cancelable, and returns a
  specific error when unavailable.
- Privilege: the selected proxyclient path is userspace and is intended to run
  without administrator rights. This still needs real non-admin validation.
- Control plane: discovery, identity, registration/payment, monitoring, and
  provider negotiation use the backend's direct HTTP/P2P clients. These direct
  connections are allowed, must be attributed to backend processes, and are
  not browser payload.
- Lifecycle: `TowerBrowser.exe` owns the Myst child. Its close path calls
  the daemon's existing `POST /stop` endpoint on port 44050, waits for graceful
  exit, and terminates only the process tree it started if graceful shutdown
  times out. The removed `/api/node/stop` path belonged to the port 44051 web UI.
- Consumer account contracts: identity details expose `balance_tokens`; a
  forced refresh is `PUT /identities/{id}/balance/refresh`; available payment
  gateways and order creation are provided by `/v2/payment-order-gateways` and
  `/v2/identities/{id}/{gateway}/payment-order`.
- Payment targets: the client independently requests `options_currency=MYST`
  and `options_currency=USD`, then exposes only the exact intersection with its
  registered CoinGate, Stripe, and PayPal adapters. CoinGate retains its
  `myst_amount`/top-level `paymentUrl` contract. Stripe and PayPal use
  two-decimal `amount_usd`, exact `USD`, empty project/caller data, and exactly
  one top-level, case-exact `checkout_url`. Unknown/case-shadowed gateways and
  recursive/nested URLs fail closed.
- Hosted payment lifecycle: the controller serializes creation, records a
  refreshed baseline MYST balance and pre-POST order IDs, never repeats an
  ambiguous POST, and reconciles only one exact newly observed intent. A
  redacted journal supports restart status polling without persisting checkout
  URLs. `paid` and balance increase remain separate facts; `confirming` and
  unknown statuses are nonterminal/pending.
- Provider contracts: WireGuard proposals are loaded from `/proposals` with
  `service_type=wireguard` and `access_policy=all`. The native adapter validates
  and deduplicates results before presenting them.
- Connection options: the pinned Go contract serializes its
  `DisableKillSwitch` field as `kill_switch`; the native request now uses that
  exact wire name and includes monitoring-failed proposals consistently with
  the upstream client.

## Native UI migration

The previous launcher made port 44051 part of normal operation in four ways:
it passed `--web-ui-port=44051`, directed the user to the page, polled
`/api/status`, and called `/api/node/stop`. The native WPF application replaces
all four dependencies:

- WPF renders the control window without HTML, WebView, or a browser.
- UI actions call `BackendController` in the same process.
- `BackendController` starts the underlying node executable directly and uses
  the pre-existing loopback-only TequilAPI on port 44050.
- `BrowserLauncher` preserves locked policy installation, isolated profiles,
  and expected-owner validation for the 4449 data-plane proxy.

No replacement UI port was introduced. The port 44050 daemon API was retained
because it is the upstream backend's existing control contract; replacing that
contract with a named pipe requires coordinated changes to `myst.exe` and is
outside this integration repository. See `NATIVE_UI_ARCHITECTURE.md`.

## Windows icon resources

The executable icon and WPF window icon have separate resource requirements.
`AppIcon.ico` is embedded in the PE for File Explorer, shortcuts, and executable
metadata. Its frames use Windows-compatible DIB encoding; the previous
PNG-compressed ICO was accepted by PE tooling but rejected by Windows Imaging
Component with `0x88982F60` when WPF loaded the XAML window.

The WPF `Window.Icon` uses the matching embedded 256 px PNG directly. Windows
CI decodes both the PNG and every ICO frame through WPF's actual
`BitmapDecoder`, preventing a shell-only icon check from missing another BAML
startup failure.

### Historical packaging discrepancy found in live testing

The older CI installer artifact for commit `227d63b` installed an automatic Windows
service named `MysteriumVPNSupervisor`. This contradicts the fork README's
"No supervisor install" claim. The tested service executable was
`resources/app.asar.unpacked/node_modules/@mysteriumnetwork/node/bin/win/x64/myst_supervisor.exe
-winservice`. The application runtime still selected the expected userspace
proxyclient path, but the installer is not acceptable for a browser-scoped
package as-is. The test installation was uninstalled and the service was
verified absent.

Version 1.0.10 continues the 1.0.6 packaging change and does not extract the backend from that installer. Packaging
pins the trusted workflow's raw `myst-windows-x64.exe` release asset and verifies
its SHA-256 before copying it into the portable bundle; no supervisor executable
or installer is included or executed.

## Tor-specific classification

Mullvad Browser already omits normal Tor daemon bootstrap, circuit UX, bridges,
pluggable transports, onion services, and Tor control-port operation. The
prototype adds none of them.

- Safe to remove/absent: Tor daemon startup, bootstrap UI, bridges/transports,
  onion services, circuit display, onion-location, Tor New Identity semantics.
- Replaced: Tor SOCKS routing is replaced by locked HTTP/CONNECT proxy policy;
  Tor DNS isolation is replaced by hostname-bearing CONNECT plus disabled
  browser DNS/DoH; Tor network fail-closed behavior is replaced by a fixed
  loopback proxy with no direct fallback.
- Dangerous to remove: anti-fingerprinting defaults, RFP, letterboxing,
  first-party isolation, telemetry/background reductions, safe external-app and
  download handling. These remain inherited from Mullvad and core items are
  locked by policy.

## Known limitations

- The wrapper is a prototype integration, not a rebuilt branded Firefox fork.
- HTTP CONNECT exposes destination hostnames to the local backend by design.
- Disabling WebRTC is stronger than proxying it and breaks WebRTC applications.
- OCSP, captive portal, extension/update, and other browser background paths
  require packet validation against the exact Mullvad release.
- Extension behavior and policy compatibility must be retested on every browser
  update. App updates are disabled so an update cannot silently replace this
  policy/build combination.
- Backend claims about SOCKS support were not confirmed by implementation;
  this integration uses HTTP/CONNECT only.
- Provider availability, identity registration, payment state, checkout
  completion, and backend control-plane behavior are external dependencies.
- Creating a fresh identity during the first run ended as `Unregistered` with
  backend log evidence `no contract code at given address`. A later run used a
  separately supplied registered identity and completed provider-path, DNS,
  WebRTC, and crash-after-connect validation. No identity key material or
  password was retained in evidence or the repository.
