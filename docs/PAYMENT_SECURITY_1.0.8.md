# Hosted payment security and provenance (1.0.8)

## Authority and architecture

The application is a native client of its existing app-owned `myst.exe` and
loopback TequilAPI. The node signs and forwards payment operations to
Mysterium's Pilvytis service, which remains the merchant-side order and wallet
credit authority. The application does not link Stripe/PayPal SDKs, possess
merchant credentials, or send requests to merchant APIs.

Primary contract evidence reviewed for 1.0.8:

- Mysterium node tag `1.38.5`, commit
  `bc017c6268383160561b431b6c339898e3acf24f`: TequilAPI gateway discovery,
  signed order create/list/get endpoints, `amount_usd`, `myst_amount`,
  `project_id`, and raw `public_gateway_data` contracts in
  `tequilapi/endpoints/pilvytis.go`, `tequilapi/contract/pilvytis.go`, and
  `pilvytis/api.go`.
- Mysterium mobile commit `510f8db1d66e5a2bbd94ce78ff0f08920f3d36b2`:
  canonical `paypal`/`stripe` gateway models and `checkout_url` mapping.
- Mysterium legacy desktop commit
  `5a330ce9a41da9826ea4486408faeff1e3a573de`: canonical gateway selection,
  USD order construction, and empty caller data for PayPal/Stripe. Its legacy
  raw-HTML flow is intentionally not reused.
- PayPal's official Orders v2 create-order documentation shows the production
  buyer approval link at exact host/path
  `https://www.paypal.com/checkoutnow?token=...`:
  <https://developer.paypal.com/sdk/orders/v2/orders-create/>.
- The standalone `mysterium-card-client` commit
  `fe21d8bc84df35754b75713f87b2c3220405c9bf` is an unofficial lifecycle
  reference only. No code/runtime dependency on that client was introduced.

Read-only discovery at
`https://pilvytis.mysterium.network/api/v2/payment/gateways?options_currency=USD`
on 2026-09-15 returned exact `stripe` and `paypal` gateways, each with `USD`
and a live minimum of 0.50. It did not create an order or expose a checkout
URL. The application floor therefore yields an effective 1.00 USD minimum for
that observed metadata.

## Fail-closed checkout boundary

Stripe and PayPal require exactly one top-level, case-exact `checkout_url`
string inside `public_gateway_data`. The parser rejects missing, alternate,
duplicate, case-shadowed, nested, non-string, and wrong-root contracts.

All targets require absolute HTTPS with a nonempty exact host, default HTTPS
port, no user-info, no fragment delimiter, no whitespace/control characters,
and no backslash. Stripe permits only `checkout.stripe.com`. PayPal permits
only `www.paypal.com` and exact path `/checkoutnow`; sandbox, localized,
subdomain, suffix, lookalike, and alternate paths fail closed. Server-provided
HTML is never rendered.

The exact initial URL currently returned by a live Mysterium PayPal order was
not observed because creating even an unpaid order was prohibited for this
release task. Consequently, Mysterium-to-PayPal runtime compatibility remains
an explicit live validation item. The client does not weaken its allowlist to
paper over that uncertainty.

## Data and lifecycle boundary

Checkout URLs exist only in a private WPF window field for the current process.
They are not journaled, logged, diagnosed, copied, or included in test fixtures
or release metadata. Card numbers, expiry, CVC/CVV, 3-D Secure information,
bank credentials, and PayPal credentials are never handled.

The redacted journal contains identity, exact gateway, order ID, requested
amount/amount currency/pay currency, expected MYST, baseline balance in wei,
timestamps, and last status. Creation is serialized; a nonterminal journal
blocks another order. A mandatory pre-POST list provides the baseline order-ID
set. Timed-out/cancelled/transport-ambiguous POSTs are never repeated and must
reconcile to exactly one newly observed exact intent. Unknown statuses remain
pending and `confirming` is not success. Only `paid` plus an independently
observed MYST balance increase is labeled end-to-end credited success.

## Prohibited live validation

No Stripe, PayPal, or CoinGate order, unpaid checkout smoke test, real payment,
card submission, PayPal login, 3-D Secure flow, or wallet-credit test occurred.
Automated build/tests and read-only gateway discovery do not establish
production payment success.
