# Privacy Browser Prototype Demo v1.0.9

Privacy Browser 1.0.9 is an unsigned Windows x64 pre-release whose primary
user-visible change is a replacement desktop/application icon made from the
user-supplied artwork: a brown square composition with a black person
silhouette in the lower-left and an Eiffel Tower outline in the upper-right.

## Application icon

- The complete supplied square image is preserved without cropping,
  stretching, redrawing, or generative alteration.
- The supplied Display P3 JPEG is decoded with its upright EXIF orientation,
  converted to sRGB, and committed as a metadata-sanitized 701 x 701 PNG.
- Deterministically generated PNG resources cover 16, 20, 24, 32, 40, 48,
  64, 128, 256, and 512 px. The Windows ICO contains DIB-compatible 16, 20,
  24, 32, 40, 48, 64, 128, and 256 px frames.
- The ICO is embedded in `PrivacyBrowser.exe` for File Explorer, shortcuts,
  and executable metadata. The matching 256 px PNG remains the WPF
  `Window.Icon` resource for reliable Windows Imaging Component decoding.
- Windows validation checks fixed artwork hashes, WPF resource decoding, all
  ICO frame sizes, and similarity between the executable's extracted PE icon
  and the approved 32 px derivative.

At very small shell sizes the full composition remains recognizable by its
brown field, dark lower-left silhouette, and upper-right tower, although the
thin landscape lines and fine tower detail necessarily become less distinct.

## Preserved behavior and security boundaries

The 1.0.8 native application, browser-scoped proxy architecture, pinned
backend and browser dependencies, bundle-integrity checks, Stripe/PayPal/
CoinGate payment adapters, fail-closed checkout validation, and payment
lifecycle handling are otherwise unchanged. The release remains unsigned.

No live Stripe, PayPal, or CoinGate order was created and no real or unpaid
checkout, payment, hosted-credential, status-transition, or wallet-credit test
occurred for 1.0.9. Production payment success is not claimed. Code signing,
clean-machine standard-user validation, reboot validation, and the documented
native-network validation gaps remain outstanding.
