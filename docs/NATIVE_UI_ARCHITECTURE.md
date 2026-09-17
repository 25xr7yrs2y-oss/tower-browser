# Native UI Architecture

## Previous implementation

The repository previously launched the `myst-lmprove` Electron executable with
`--web-ui-port=44051`. That executable started two child-facing surfaces:

1. A Node.js HTTP server on `127.0.0.1:44051` served HTML and implemented a
   convenience REST facade such as `/api/status`, `/api/connect`, and
   `/api/node/stop`.
2. The Myst daemon exposed its existing TequilAPI control contract on
   `127.0.0.1:44050` and its browser-facing HTTP/CONNECT proxy on
   `127.0.0.1:4449` after provider connection.

The launcher depended on the first surface to poll readiness, tell the user
where to complete setup, and stop the backend. The HTML frontend also called
the facade, which in turn called TequilAPI. This made a browser-accessed
loopback HTTP server part of the UI dependency graph:

```text
default browser -> 44051 HTML/API server -> 44050 TequilAPI -> Myst backend
Mullvad Browser -> 4449 HTTP/CONNECT proxy -> provider
```

Port 44051 added no backend capability. It was an adapter and static-file host
for the web UI, so its TCP dependency was unnecessary for local presentation.
In a TUN/WFP/system-proxy environment it could fail independently of the daemon
and provider proxy.

## Current implementation

The application now uses a native .NET 8 WPF window:

```text
WPF controls -> in-process BackendController -> 44050 TequilAPI -> Myst backend
                                               |
Mullvad Browser -> locked 127.0.0.1:4449 -------+-> provider
```

`TowerBrowser.exe` starts the bundled `myst.exe` directly with
`--ui.enable=false`, `--usermode`, `--proxymode`, an explicit loopback bind, and
the same consumer/discovery settings used by the audited Electron backend. It
therefore never starts the Electron process or the 44051 server.

The upper-right **Controls** entry opens the native control panel. It supports:

- backend status and lifecycle;
- consumer terms acceptance using the daemon's terms contract;
- passphrase-protected identity creation, encrypted-key import, explicit
  multi-identity selection, unlock, and registration;
- wallet balance refresh and native payment-order creation through the
  daemon's `/v2/payment-order-*` contracts;
- WireGuard provider discovery, pricing details, and selection;
- connect and disconnect;
- guarded launch and process tracking of the isolated Mullvad Browser profile;
- local activity reporting with translated backend errors.

Snapshot reads isolate the connection, identity, and terms resources. A
registration-chain timeout can therefore mark only identity status as
unavailable while keeping backend status, provider refresh, and retry controls
usable. Action requests surface progress and their final success or failure in
the WPF panel instead of relying on raw daemon output.

The UI and controller communicate through ordinary in-process method calls—no
socket, local web server, WebView, or frontend IPC bridge is needed. The native
app uses `HttpClient` only at the backend adapter boundary for the existing
TequilAPI contract. System HTTP proxies are disabled for those loopback calls.

## Port decisions

| Port | Purpose | Decision |
|---|---|---|
| 44051 | Electron HTML/UI facade | Removed from startup and all code paths |
| 44050 | Myst daemon TequilAPI | Retained as the upstream backend contract, bound to loopback |
| 4449 | Browser payload HTTP/CONNECT proxy | Retained unchanged |

Introducing a named-pipe sidecar that merely translated back to 44050 would
add process and protocol complexity without eliminating the daemon listener.
A true named-pipe migration should be implemented in the Myst daemon itself,
then consumed by `BackendController`. That is a compatible future adapter
change because WPF does not know which transport the controller uses.

## Safety properties retained

- The native app does not change system proxy, DNS, firewall, or route state.
- Browser policy remains locked to `127.0.0.1:4449` with no direct fallback.
- The app refuses to overwrite an unrelated browser policy file.
- Before browser launch, one readiness evaluator verifies connection state,
  browser and policy integrity, loopback-only port 4449 binding, and ownership
  by the app-started `myst.exe`. The UI uses the same result.
- Adopted backends remain available for control-plane diagnostics but cannot
  launch the browser because their proxy ownership is unverifiable.
- A portable bundle manifest detects missing or mixed controller, browser,
  backend, and policy components before startup.
- Backend shutdown first uses the graceful daemon endpoint and only then kills
  the owned process tree if required.

## Deliberate limitations

- Identity export/removal and OS-backed credential storage are not implemented.
  Passphrases remain operation-scoped and are not persisted.
- Port 44050 remains until Myst gains an equivalent named-pipe or in-process
  control contract.
- Provider, payment, registration, and discovery behavior remains external to
  this repository. The native UI consumes the backend contracts but does not
  reimplement Mysterium's payment or discovery services.
