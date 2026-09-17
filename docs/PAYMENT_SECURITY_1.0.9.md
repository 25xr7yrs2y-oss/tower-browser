# Hosted payment security and provenance (1.0.9)

## Unchanged payment authority and architecture

Version 1.0.9 changes the desktop/application artwork and does not change the
1.0.8 payment implementation. The application remains a native client of its
app-owned `myst.exe` and loopback TequilAPI. The node signs and forwards
payment operations to Mysterium's Pilvytis service, which remains the
merchant-side order and wallet-credit authority. The application does not
link Stripe/PayPal SDKs, possess merchant credentials, or send requests to
merchant APIs.

The 1.0.8 contract evidence remains applicable: Mysterium node tag `1.38.5`
commit `bc017c6268383160561b431b6c339898e3acf24f`, Mysterium mobile commit
`510f8db1d66e5a2bbd94ce78ff0f08920f3d36b2`, and Mysterium legacy desktop
commit `5a330ce9a41da9826ea4486408faeff1e3a573de`. PayPal's official Orders v2
create-order documentation identifies the production buyer approval target at
exact host/path `https://www.paypal.com/checkoutnow?token=...`.

Read-only discovery on 2026-09-15 reported exact `stripe` and `paypal`
gateways, each with `USD` and a 0.50 live minimum. The application's own floor
therefore yields an effective 1.00 USD minimum for that observed metadata.

## Fail-closed checkout boundary

Stripe and PayPal require exactly one top-level, case-exact `checkout_url`
string inside `public_gateway_data`. Missing, alternate, duplicate,
case-shadowed, nested, non-string, and wrong-root contracts are rejected.

All targets require absolute HTTPS with a nonempty exact host, default HTTPS
port, no user-info, no fragment delimiter, no whitespace/control characters,
and no backslash. Stripe permits only `checkout.stripe.com`. PayPal permits
only `www.paypal.com` and exact path `/checkoutnow`; sandbox, localized,
subdomain, suffix, lookalike, and alternate paths fail closed. Server-provided
HTML is never rendered.

Checkout URLs exist only in native memory. They are not journaled, logged,
diagnosed, copied, or included in release metadata. Card numbers, expiry,
CVC/CVV, 3-D Secure information, bank credentials, and PayPal credentials are
never handled.

The redacted journal and order reconciliation behavior are unchanged from
1.0.8. Creation remains serialized; ambiguous POST outcomes are never retried;
unknown statuses stay pending; `confirming` is not success; and only `paid`
plus an independently observed MYST balance increase is labeled end-to-end
credited success.

## Prohibited live validation

No Stripe, PayPal, or CoinGate order, unpaid checkout smoke test, real payment,
card submission, PayPal login, 3-D Secure flow, or wallet-credit test occurred
for 1.0.9. Automated build/tests do not establish production payment success.
The exact initial URL returned by a live Mysterium PayPal order remains an
explicit future validation item; the client continues to fail closed unless it
matches the documented production contract.
