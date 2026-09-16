# Validation Results

## Current status

### Version 1.0.9 validation scope

The 1.0.9 automated suite retains the 1.0.8 Mysterium-hosted Stripe/PayPal client
contract validation with deterministic fake TequilAPI responses and static native-UI
invariants. It also validates fixed hashes for the supplied icon artwork and its
derived WPF/Windows assets, WIC decoding of every ICO frame, and comparison of
the built executable's extracted PE icon with the approved 32 px artwork.
Read-only production discovery on 2026-09-15 reported exact
`stripe` and `paypal` gateways with USD currency and a 0.50 live minimum, so
the application floor makes 1.00 USD the effective minimum and lowest preset.

No live order was created. No real or unpaid transaction, hosted checkout
session, card submission, PayPal login, 3-D Secure step, or MYST wallet-credit
test was performed. Production payment success therefore remains unvalidated;
the exact Mysterium PayPal checkout URL contract is deliberately still a live
runtime validation item and will fail closed unless it matches the documented
exact `https://www.paypal.com/checkoutnow` initial contract.

> The packet captures below predate the native UI migration. They remain
> evidence for the unchanged browser policy and 4449 data plane, but they do
> not constitute network validation of the native application. Version 1.0.1
> received the focused Windows startup validation documented below; no new
> proxy, TUN-mode, leak, or packet-capture claim is inferred from that run.

### Version 1.0.1 Windows startup regression

On 2026-07-15, the released 1.0.0 executable reproduced the startup failure on
the Windows test device: the process displayed a
`System.Windows.Baml2006.TypeConverterMarkupExtension` error before its main
window was created. Windows WPF `BitmapDecoder` rejected the PNG-compressed ICO
with `FileFormatException` and WIC HRESULT `0x88982F60`.

The matching 256 px PNG decoded successfully. The regenerated DIB-frame ICO
also decoded all nine sizes (16, 20, 24, 32, 40, 48, 64, 128, and 256 px).
A self-contained 1.0.1 fix build then opened the native WPF window in the
interactive console session with the official title-bar icon visible. No
`state/startup-error.log` was created, the owned `myst.exe` child started, port
44050 was loopback-only, and no listener existed on the removed UI port 44051.

The backend subsequently reported a separate 10-second control request timeout
on that device. This did not prevent the native window from starting and was
not treated as evidence of external proxy or TUN-mode compatibility.

**Prototype works for the tested browser-scoped routing paths, with packaging
and operational gaps.** Tests used Windows Server 2022, Mullvad Browser
15.0.14, and the backend CI artifact for commit `227d63b`. A supplied registered
identity allowed the previously blocked provider run. Direct host egress was
`54.168.34.136`; the loopback proxy exited at `64.110.102.90`.

| Scenario | Actual result | Evidence | Result |
|---|---|---|---|
| Configuration invariants | Locked loopback proxy, DoH/DNS prefetch off, WebRTC off, RFP and letterboxing on | `tests/Test-Configuration.ps1` | Pass |
| Native application build | .NET 8 WPF source compiles and publishes as a Windows executable | GitHub Actions `Build.ps1` | Pass |
| Launcher safety invariants | Delegates to the native application; no web UI URL/API or system network mutation | `tests/Test-Launcher.ps1` | Pass by source invariant |
| Native architecture invariants | Direct `myst.exe` ownership, disabled web UI, loopback-only daemon control, 4449 ownership check, upper-right Controls entry | `tests/Test-NativeArchitecture.ps1` | Pass by source invariant |
| Evidence integrity | Retained capture hashes and recorded routing counters match the documented results | `tests/Test-Evidence.ps1` | Pass |
| S1 other apps unaffected | Exact PID trees for `curl` and `Invoke-WebRequest`: 80 socket records, 40 direct records to `162.159.140.220:443`, zero records involving 4449; direct IP `54.168.34.136`. An exploratory Edge run loaded a 564-byte page with no 4449 use, but its exact-PID rerun under SYSTEM did not render. | `evidence/windows-20260618/other-apps/` | Pass for curl/IWR; Edge evidence limited |
| S2 browser payload scoped | Proxy listener owned by bundled `myst.exe` on `127.0.0.1:4449`. Browser: 433 TCP observations, 173 established proxy records, zero non-loopback records. Direct and proxy public IPs differed. | `evidence/windows-20260618/provider-payload/` | Pass |
| S3 DNS | Browser recorded zero TCP 53/853 and zero UDP endpoints while loading HTTP and HTTPS through the provider. Hostnames were carried to the HTTP/CONNECT backend. | Provider pcapng and socket maps | Pass |
| S4 WebRTC | Loaded a WebRTC leak-test page with WebRTC locked off: zero browser UDP endpoints and zero non-loopback browser TCP records. | Provider pcapng and socket maps | Pass |
| S5a backend absent at launch | 309 browser socket records: 96 `SynSent` attempts to `127.0.0.1:4449`, 78 internal loopback records, 135 bound sockets, and zero non-loopback browser sockets. | `evidence/windows-20260618/fail-closed/` | Pass |
| S5b backend crashes after launch | Killed the process owning 4449, verified listener absent, then launched a fresh browser: 78 `SynSent` attempts to 4449 and zero direct records. | `evidence/windows-20260618/backend-crash/` | Pass |
| S6 cleanup/persistence | Before/after user proxy unset; WinHTTP direct; DNS unchanged; default route unchanged. Both uninstallers exited 0; service/process/test paths absent. Reboot was not performed. | Results summarized here | Pass without reboot; reboot inconclusive |
| S7 non-admin | Source path is userspace, but live run used the Administrator account and the installer created an automatic supervisor service. | Source + service observation | Fail for installer; runtime non-admin inconclusive |

## Captured artifacts

- `fail-closed/capture.pcapng`: 1,280 bytes, SHA-256 recorded in the evidence README.
- `fail-closed/browser-sockets.csv`: time-aligned browser PID socket map.
- `other-apps/capture.pcapng`: 7,472,376 bytes.
- `other-apps/process-map.csv` and `sockets.csv`: exact root PID and descendant attribution.
- `provider-payload/capture.pcapng`: active-provider browser payload capture,
  paired browser/backend socket maps, and exact process map.
- `backend-crash/capture.pcapng`: post-termination fail-closed capture and
  browser socket/process maps.

## Host changes and cleanup

- Temporarily installed Mullvad Browser and the backend into
  `C:\Users\Administrator\Desktop\privacy-browser-live`.
- The backend installer created and started automatic service
  `MysteriumVPNSupervisor`; its uninstaller removed the service.
- A failed fresh identity remains in pre-existing Administrator `.mysterium`
  state because deleting shared prior state would be destructive. The supplied
  registered identity was imported only into isolated SYSTEM-profile test
  state, which was deleted after capture.
- Restarted the pre-existing `sshd` service to recover repeated pre-key-exchange
  resets; it finished Running/Automatic.
- Stopped test browser, backend, Edge, curl, and PowerShell child processes.
- Removed the live bundle and remote project copy after collecting evidence.
- Transferred the supplied identity through temporary private encrypted
  objects with expiring URLs. The objects and all remote key/password files
  were verified absent after testing. No secret appears in committed artifacts.
