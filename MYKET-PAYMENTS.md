# Myket payment deployment

The Myket purchase implementation supersedes the earlier read-only/payment-unavailable subscription baseline in `SUBSCRIPTIONS.md`. Payments remain disabled by default. Mobile implementation/setup details are in `../../lm/docs/myket-payments.md` in the combined workspace.

## Required configuration

Set these through private backend environment settings, not mobile environment files:

```text
Payments__Myket__Enabled=true
Payments__Myket__PackageName=<reserved package>
Payments__Myket__PublicKey=<base64 RSA public key>
Payments__Myket__AccessToken=<server-only Myket API token>
Payments__Myket__Products__1=<one-month SKU>
Payments__Myket__Products__6=<six-month SKU>
Payments__Myket__Products__12=<twelve-month SKU>
```

Use three distinct consumable products, not auto-renewing store subscriptions. Match the existing server catalog prices: 70,000, 294,000 and 420,000 toman for 1, 6 and 12 months. Confirm the store console's currency. The installed package and reserved Myket package must match exactly.

Apply additive migration `20261026120851_AddMyketOrders` using the normal backed-up SQL Server deployment process, rebuild/publish the API and restart it before enabling payments. No live migration/deployment or real charge has been performed by this task. Do not deploy stale existing `backend/publish` binaries.

## Contracts and safeguards

All `/api/v1/payments/myket` routes require an active authenticated session, use no-store responses and are rate limited. GET `/` returns only the public key, package and SKUs. POST `/orders` accepts a supported duration and creates/reuses an owner-bound, one-hour checkout reservation with a cryptographically random payload. POST `/verify` accepts only SKU/token, calls the official Myket server verification endpoint and matches the trusted provider payload to the owner/order before activating Plus. POST `/orders/{id}/cancel` only cancels an unfinished order belonging to that owner; it does not issue a refund.

The verification transport is POST `https://developer.myket.ir/api/partners/applications/{package}/purchases/products/{sku}/verify`, header `X-Access-Token`, JSON `{ "tokenId": "..." }`. Official documentation checked October 26, 2026: `https://myket.ir/kb/pages/server-to-server-payment-validation-api/`. Redirects are disabled, HTTP calls time out and secret headers are redacted. Missing/malformed/provider-error responses fail closed.

Order/activation writes are atomic and serialized by an owner row lock. A filtered unique provider-token hash index prevents receipt reuse. Paid access starts at the original verified purchase time, not recovery time. Repeated verification never extends a period. Active/scheduled subscriptions block new checkout and overlapping activation. Expired Active rows are retired transactionally. The app consumes a receipt only after successful server fulfillment, so failures remain recoverable in the original account.

New mobile clients opt in to purchasable catalog flags using `X-Lerona-Payments: myket-v1`; the response varies by that header. Older clients receive disabled purchase flags and their existing entitlement read contracts remain unchanged, so enabling billing does not break older clients' strict validators. Legacy entitlement `isPurchasable`/`purchaseSupported` fields remain false; the new checkout UI uses the opt-in catalog.

## Acceptance before public release

Automated tests cover ownership, active overlap, expiry, pending intent retries, provider states/errors, unique replay protection, original purchase dates, legacy catalog compatibility and authentication. They do not replace live Myket/device acceptance or SQL Server concurrency testing. Test cancellation, process death, offline verification, interrupted consumption, account switching and simultaneous devices with the actual reserved/signed app.

Automatic refunds/revocations, store webhooks and financial administration are not implemented. Define a reconciliation/refund procedure before public release. A paid overlapping or foreign-account receipt remains unconsumed for recovery/support; it must not be silently discarded or grant a second overlapping subscription. Existing NuGet security advisories for `System.Security.Cryptography.Pkcs` 9.0.11 also require separate review before production.

## Validation record — October 26, 2026

The solution builds and all 60 focused Myket, verifier, subscription and persistence tests pass. The broader suite reports 198 passing tests and one separate sync/card-read failure because SQLite cannot order the existing `DateTimeOffset` column; that unrelated query/test-provider issue is not modified. These results are not a production SQL Server concurrency or real-payment acceptance test. The migration has not been applied to a live database.
