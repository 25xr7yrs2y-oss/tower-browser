# Privacy Browser Prototype Demo v1.0.8

Privacy Browser 1.0.8 is an unsigned Windows x64 pre-release adding
production-oriented Mysterium-hosted credit/debit-card checkout through the
canonical `stripe` gateway and PayPal checkout through the canonical `paypal`
gateway. CoinGate and every browser-scoped privacy, ownership, fail-closed,
packaging, and source-offer invariant remain in place.

## Stripe card and PayPal top-ups

- The native WPF wallet discovers both MYST- and USD-denominated gateway
  options from the app-owned loopback TequilAPI. It displays only exact,
  case-sensitive live gateway names that also have registered local adapters.
- “Credit / debit card (Stripe)” and “PayPal” use `amount_usd`, exact `USD`, a
  validated country/state, empty `project_id`, and empty
  `gateway_caller_data`. CoinGate remains a separate MYST-denominated
  `myst_amount` path.
- Stripe and PayPal have a 1.00 USD application floor. The effective minimum is
  the greater of 1.00 USD and the current live minimum. The 1.00 preset appears
  only when live metadata permits it; a higher live minimum always wins.
- Fiat input accepts only unsigned, non-exponent decimal text with at most two
  fractional digits, is serialized with exactly two digits, and is capped at
  1000.00 USD.

## Security and lifecycle

- The app has no Stripe/PayPal merchant API integration, SDK, WebView, or raw
  HTML/`secure_form` renderer. Mysterium/Pilvytis remains the order and wallet
  credit authority. Card, CVC/CVV, expiry, 3-D Secure, bank, and PayPal
  credentials never enter this application.
- Stripe and PayPal require exactly one top-level, case-exact `checkout_url`
  string. Checkout is opened only in the default system browser after strict
  HTTPS/default-port, user-info, fragment, whitespace/control, and exact-host
  validation. Stripe accepts only `checkout.stripe.com`; PayPal accepts only
  the official production `www.paypal.com/checkoutnow` initial contract.
- Checkout URLs remain only in native memory. They are not persisted, logged,
  copied, or exposed through diagnostics. Nested, duplicate, case-shadowed,
  sandbox, suffix/lookalike, and alternate-host targets fail closed.
- Creation is serialized. Before POST, the app refreshes the identity's
  baseline MYST balance and records existing order IDs. A timed-out,
  caller-cancelled, or transport-ambiguous POST is never retried; signed order
  listing must find exactly one new exact-intent candidate or the operation
  remains blocked as ambiguous.
- A redacted journal records only resumable order metadata. Signed per-order
  polling uses bounded backoff. Unknown statuses stay pending, `confirming` is
  not final, and Mysterium's `paid` status is displayed separately from an
  independently observed MYST balance increase. Only both facts are presented
  as end-to-end credited success.

## Validation and limitations

Windows GitHub Actions build the WPF application, smoke-start it, run the
existing privacy/ownership/package checks, and run focused hosted-payment
runtime/static tests for discovery, amount rules, response and URL attacks,
intent reconciliation, single-active-order enforcement, journal resume,
status mapping, and balance evidence. The release-package workflow verifies
the portable ZIP, component manifest, checksums, pinned backend, and
corresponding-source archive before publication.

No live Stripe, PayPal, or CoinGate order was created and no payment or unpaid
order smoke test occurred for this release. Production payment success is not
claimed. A future explicitly authorized live cycle must validate actual
Mysterium checkout response shapes (especially the exact initial PayPal
host/path), redirects, payment-provider UX, status transitions, and final MYST
wallet credit. Code signing, clean-machine standard-user validation, reboot
validation, and the previously documented native-network validation gaps also
remain outstanding.
