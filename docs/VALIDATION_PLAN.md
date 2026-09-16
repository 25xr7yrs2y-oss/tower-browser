# Packet-Capture Validation Contract

No browser-only routing claim is valid until every mandatory scenario below has
a packet capture, process/port mapping, result, and retained evidence file.

## Tools and attribution

- `pktmon` captures packets on all Windows networking components and converts
  ETL to pcapng for Wireshark/tshark inspection.
- `Get-NetTCPConnection`, `Get-NetUDPEndpoint`, and `Get-Process` map local ports
  and owning PIDs before, during, and after each scenario.
- `netstat -abno` is retained as a second process/socket snapshot.
- Route, DNS, adapter, WinHTTP proxy, and per-user Internet Settings snapshots
  are taken before and after the run.
- Browser payload is traffic caused by a test URL loaded in the modified
  browser. Expected browser egress is loopback TCP to `127.0.0.1:4449`.
- Direct traffic owned by `myst.exe` or the packaged backend is classified as
  allowed backend control/provider-plane traffic and listed separately.
- Any non-loopback TCP/UDP owned by the modified browser is a failure, except
  traffic explicitly proven to be loopback management traffic.

## Scenarios

| ID | Scenario | Expected result | Failure condition |
|---|---|---|---|
| S1 | Baseline external browser, curl, Invoke-WebRequest | Direct system path; no connection to 4449 | Any forced use of 4449 or changed system settings |
| S2 | Modified browser HTTP and HTTPS | Browser uses loopback 4449; backend egress is separate | Browser-owned non-loopback destination traffic |
| S3 | DNS | No browser-owned UDP/TCP 53 or 853 and no browser DoH | Browser DNS outside loopback proxy model |
| S4 | WebRTC test page | No browser STUN/TURN/ICE UDP egress | Any direct browser-owned ICE path |
| S5a | Backend absent at launch | Page load fails; no direct browser egress | Destination succeeds or browser egresses directly |
| S5b | Backend killed after launch | Existing/new loads fail; no fallback | Any direct fallback after kill |
| S6 | Close browser and backend; reboot comparison | Routes, DNS, adapters, global proxy unchanged | Persistent system networking change |
| S7 | Standard-user launch | Full operation without elevation | Elevation required or privileged system mutation |

## Hosted payment validation boundary

Automated 1.0.9 validation must exercise discovery intersection, amount and
request serialization, hostile checkout URL inputs, response-intent matching,
ambiguous POST reconciliation, one-active-order enforcement, journal restart,
status mapping, and independent balance evidence with local fake responses.
Read-only production gateway discovery is permitted.

It must not create a Stripe, PayPal, or CoinGate order or submit card, PayPal,
bank, or 3-D Secure credentials. A later explicitly authorized live validation
must separately confirm Mysterium's actual top-level `checkout_url`, the exact
initial PayPal production host/path, provider redirects, status transitions,
and wallet credit. Compilation, mocks, metadata discovery, and opening an URL
cannot establish production payment success.

## Capture procedure

Run `validation/Invoke-Validation.ps1` from an elevated PowerShell prompt. For
each interactive browser scenario, start a fresh capture, record socket maps at
the action boundary, perform one named action, stop capture, convert to pcapng,
and fill in `VALIDATION_RESULTS.md`. Do not combine unrelated traffic in one
capture when separate captures are practical.

For S2-S5, record the browser PID tree and filter the socket snapshots by those
PIDs. Packet captures alone do not carry Windows PID metadata; the time-aligned
socket snapshots are therefore mandatory attribution evidence.
