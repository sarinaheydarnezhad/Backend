# Phase 7A: Itatest commerce audit and frontend API handoff

Audit date: 2026-11-19. Repository: `D:\Lerona\Backend` (confirmed with `Get-Location` and `git rev-parse --show-toplevel`; initial working tree clean).

## Scope and integration boundaries

Itatest is the existing **Cent** module in the shared Lerona API host. All paths below are relative to `/api/cent/v1`. No frontend implementation is included in Phase 7A.

| Component | Existing implementation |
| --- | --- |
| Composition, authorization, errors, HTTP client | `backend/Cent.Module/CentModule.cs` |
| Commerce entities | `backend/Cent.Module/Entities.cs`: `CentProduct`, `CentOrder`, `CentPaymentEvent`, `CentEntitlement` |
| Persistence | `CentDbContext.cs`: separate `cent_*` tables, foreign keys only within Cent; `cent_migrations_history` |
| Checkout, Zarinpal adapter, verification, callback, history | `CentPayments.cs` |
| Catalog, library, access evaluation, protected page delivery | `CentContent.cs` |
| Training/product administration, orders, revocation, audit | `CentAdmin.cs` |
| Protected source lessons and simulator | `CentLearning.cs` / `CentPractice.cs`, using `CentAccess` |
| Local mock host | `backend/Cent.Preview`; Development-only SQLite host |

Production persistence uses SQL Server. `ConnectionStrings:CentDb` takes precedence; otherwise Cent shares the physical `FlashcardsDb` database. Cent sessions, users, entitlements, and payment provider are separate from Lerona JWT authentication, subscriptions, and Myket payments. This phase changes only Cent commerce code, adds Cent tests, and adds this document. No shared host, authentication, flashcard, backup, synchronization, dependency, model, or migration changes are required.

## Capability audit

| Capability | Result |
| --- | --- |
| Educational PDF products | Implemented as a `TRAINING` product covering a training's published lessons; no separately priced page/document product. PDFs are converted in the administrator frontend to protected WebP pages. Original PDFs are not delivered by this API. |
| Pricing | Already server-authoritative: positive integer `PriceRial`; administrator API permits up to 1,000,000,000,000 rials. Checkout accepts only product/request identifiers. |
| Availability | New orders require active products and published trainings. Corrected initiation also checks availability when resuming an order without an authority. |
| Individual purchases | Implemented as permanent training grants. A non-revoked training grant blocks a new purchase of that training. Subscription access does not block an individual purchase. |
| Subscription plans | Existing administrator-managed products, 1–366 days; no hardcoded commercial tier list. Active plans come from catalog. Renewals are separate explicit purchases, not automatic billing. |
| Orders/history | Implemented; immutable purchase snapshot of title, kind, training, duration, amount, and provider. Owner history limited to latest 200; admin history latest 500. No pagination or dedicated order-detail GET. |
| Checkout | One product per request; no cart. Required user-scoped UUID request key. Duplicate initiation race corrected. |
| Payment provider | Zarinpal v4 request/verify; sandbox and live modes. Stored order environment controls provider calls. No real payment executed in this audit. |
| Mock payments | Development plus explicit configuration only; completes synchronously during checkout. Corrected checkout response now reports `PAID`. |
| Payment verification | Server-to-provider verification with stored authority and rial amount; codes 100/101 plus a valid reference required. Callback status alone never grants access. |
| Completion | Already transactional: order payment update, successful verification event, and exactly one entitlement commit together. Existing per-user write lock serializes renewals. Unique order entitlement and provider/reference constraints prevent duplicate grants/reference reuse. |
| Resource protection | Already authenticated, checked on every page/source read; private/no-store, nosniff headers; no original-PDF route. Published document pages are immutable. |
| Discounts | Unsupported: no coupon, promotion, discount calculation, or discount administrator API. |
| Refund/cancellation processing | Unsupported: no provider refund/reversal API or automatic grant revocation from refunds. Administrative grant revocation is access control only. |
| Failure/cancellation state | Only `PENDING` and `PAID` are persisted. Pending includes uncompleted, rejected, failed, cancelled, and uncertain attempts. No invented terminal states were added. |

There is no built-in commercial price/plan seed in the module, preview source, or curriculum importer. Curriculum import creates training/content navigation without assigning product prices. Actual deployed products, prices, active flags, historical orders, and grants were not enumerated from a shared/live database; obtain them through the existing authorized catalog/admin APIs during frontend integration. Example prices in this document and tests are disposable illustrations.

## Confirmed defects and corrections

1. **Concurrent checkout retries could create multiple provider authorities.** The previous flow requested authorities concurrently and retained just the winning database value. Authority creation now takes the same per-Cent-user database write lock used by completion, reloads the order, and calls the provider only if it still needs an authority. The authority and REQUEST event persist in that transaction. The initial purchase snapshot remains persisted if initiation fails. This works across API instances through the database, without a process-local lock or schema change.
2. **Mock checkout returned stale `PENDING`.** Completion clears tracking; checkout now reads the persisted order after mock confirmation and returns `PAID` with the authoritative `amountRial`.
3. **Mock configuration could override an existing real order during initiation.** Request dispatch now uses the stored order provider, as verification does. Real orders require a valid nonempty merchant UUID even if development mock is currently enabled. Unknown providers and disabled mock verification fail closed.
4. **Malformed provider payloads could throw unstructured parsing/type errors or accept arbitrary reference JSON.** JSON shape, numeric result codes, authority strings, and positive numeric reference identifiers are validated. Invalid provider responses produce `502 gateway_invalid_response`; negative verification codes in Zarinpal's `errors` object on successful HTTP responses are retained in payment events. HTTP failures retain the existing 503 recovery behavior. No raw provider error/body/card details are returned or persisted.
5. **Pending orders without an authority could initiate after becoming unavailable or already owned.** Initiation rechecks product active status, training publication, and existing training ownership under the purchase lock. Orders with an existing authority can still be resumed/verified against their original snapshot, preserving legitimate in-flight payments.
6. **History lacked identifiers needed to resume interrupted checkout.** Additive `productId`, `requestId`, `trainingId`, and `durationDays` expose the existing snapshot/retry key to the owner. Checkout additionally returns `amountRial` so the frontend does not substitute a changed catalog price.

Paid checkout retries return the saved paid result without requiring the current merchant configuration. A valid `ClientBaseUrl` is still required to construct their redirect.

## Authentication and error envelope

`CentUser` means `Authorization: Bearer <Cent session token>`, obtained using the existing Cent sign-in API. Lerona JWTs cannot substitute for Cent sessions. `CentAdmin` additionally requires the Cent `ADMIN` role. Existing sessions expire in seven days and logout revokes them. Authentication implementation is unchanged.

Business errors use JSON `{ "message": "localized user message", "code": "stable_code" }`. Use the status and code for logic; do not parse translated messages. Authentication failures and admission limits may have a different or empty body.

| HTTP | Code | Frontend action |
| --- | --- | --- |
| 400 | `invalid_request` | Correct missing/empty `requestId` or invalid input. |
| 401 | Framework authorization | Sign in again and preserve the pending order/retry key. |
| 403 | `access_required` | Refresh library; show purchase/subscription requirement. |
| 403 | `mock_payment_disabled` | Operator/environment issue; do not report paid or create a replacement payment. |
| 404 | `invalid_request` | Product/training/order not found or unavailable; another user's order also appears not found. |
| 409 | `already_owned` | Refresh library and offer reading. |
| 409 | `invalid_request` | Request key reused for another product, or authority not ready at service level. Preserve the original key/product pairing. |
| 409 | `invalid_payment_provider` | Operator investigation of an invalid stored provider. |
| 502 | `gateway_rejected` | Initiation rejected; retry the original checkout request after user action. |
| 502 | `gateway_invalid_response` | Payment state is uncertain/unconfirmed. Refresh order and retry verification; never grant access locally. |
| 503 | `payments_not_configured` | Disable checkout UI and report temporary payment configuration unavailability. |
| 503 | `temporarily_unavailable` | Database, network, timeout, or HTTP provider failure. Retry verification or the original request key as appropriate. |
| 429 | May have no JSON code | Respect `Retry-After`, avoid rapid polling. |

Other administrator validation errors generally use `invalid_request` with their documented HTTP status. Existing Cent exception logging records exception type and trace ID without provider request/response bodies or credentials. Payment-event rows store action, integer code, order ID, and timestamp. Hosting/reverse-proxy log configuration is outside this audit; avoid logging authorization headers, provider bodies, or callback query values there.

## Monetary units and purchase snapshots

All `priceRial` and `amountRial` values are integer **Iranian rials (IRR)**, not tomans. Zarinpal request explicitly sends `currency: "IRR"`; verify sends the same stored rial amount. The existing order schema has no currency column because the module supports only IRR. Do not send another currency or a client-calculated price.

If a display uses tomans, divide rials by 10 for presentation and label the unit. Never multiply a rial field again before checkout. Product prices, titles, and subscription duration can change; existing orders retain their original values. Training/kind cannot be changed on products that have any order. Availability checks gate new initiation; trusted completion honors the original order even if the product is subsequently deactivated or unpublished. An unpublished training remains unreadable until published again.

## Public and customer API

| Method/path | Authorization | Request / response |
| --- | --- | --- |
| `GET /status` | Public | `{ service: "Cent", payments: "DEVELOPMENT_MOCK" \| "NOT_CONFIGURED" \| "SANDBOX" \| "LIVE", screenshotBlocking: false }`; mode is informational, not a checkout readiness guarantee. |
| `GET /catalog` | Public | `{ trainings: [...], products: [...] }`; published training metadata and active purchasable products, sorted by price. No ownership state. |
| `POST /checkout` | CentUser | Body `{ productId, requestId }`; `{ id, redirectUrl, status, amountRial }`. |
| `GET /me/orders` | CentUser | Latest 200 owner orders, newest first. Fields shown below. |
| `POST /me/orders/{id}/verify` | CentUser | Empty body; `{ paid: true/false }`. Foreign/unknown ID: 404. No authority yet: `paid:false`. |
| `GET /payments/callback?Authority=...&Status=OK` | Public/provider browser return | Resolves the stored authority; calls server verification only for `OK`; then 302 to configured result URL. `NOK` does not grant or revoke access. |
| `GET /me/library` | CentUser | `{ trainings, documents, progress, notes, entitlements, hasSubscription, readableTrainingIds }`; authenticated source of access and historical grant data. |
| `GET /documents/{id}` | CentUser + current access | `{ document, sections, notes, progress, watermark }`; per-user private notes and watermark. |
| `GET /documents/{id}/pages/{page}` | CentUser + current access | `image/webp`; no range processing; never a raw PDF. |
| `GET /documents/{id}/source-sections` | CentUser + current access | Published source lesson sections, using the same access gate. |
| `GET /me/notes` | CentUser | Owner notes remain available after access expiration. |
| `PUT /notes/{id}` | CentUser + current access | Existing note editing contract unchanged; denied after expiry without another valid grant. |

Catalog example (illustrative UUIDs; database values determine the actual products):

```json
{
  "trainings": [{ "id": "11111111-1111-1111-1111-111111111111", "title": "Algebra", "description": "Course lessons", "includedInSubscription": true, "published": true, "createdAt": "2026-11-19T08:00:00+00:00" }],
  "products": [
    { "id": "22222222-2222-2222-2222-222222222222", "title": "Algebra access", "kind": "TRAINING", "trainingId": "11111111-1111-1111-1111-111111111111", "priceRial": 250000, "durationDays": null, "active": true },
    { "id": "33333333-3333-3333-3333-333333333333", "title": "Monthly", "kind": "SUBSCRIPTION", "trainingId": null, "priceRial": 100000, "durationDays": 30, "active": true }
  ]
}
```

Checkout request:

```http
POST /api/cent/v1/checkout
Authorization: Bearer <Cent session token>
Content-Type: application/json

{"productId":"22222222-2222-2222-2222-222222222222","requestId":"44444444-4444-4444-4444-444444444444"}
```

Real/sandbox initiation response example:

```json
{"id":"55555555-5555-5555-5555-555555555555","redirectUrl":"https://sandbox.zarinpal.com/pg/StartPay/A000000000000000000000000000000000001","status":"PENDING","amountRial":250000}
```

Development mock completes immediately and instead returns `status: "PAID"` and a frontend result URL. Paid checkout retry does the same. Result URL is always `<Cent:ClientBaseUrl>/checkout/success?order=<order UUID>`; the path name does **not** mean payment succeeded.

Order history example:

```json
[
  {
    "id":"55555555-5555-5555-5555-555555555555",
    "title":"Algebra access","kind":"TRAINING","amountRial":250000,
    "status":"PAID","provider":"SANDBOX","reference":"123456",
    "createdAt":"2026-11-19T08:00:00+00:00","paidAt":"2026-11-19T08:01:00+00:00",
    "productId":"22222222-2222-2222-2222-222222222222",
    "requestId":"44444444-4444-4444-4444-444444444444",
    "trainingId":"11111111-1111-1111-1111-111111111111","durationDays":null
  }
]
```

An entitlement in `/me/library` contains `id`, `userId`, `orderId`, `trainingId`, `kind`, `startsAt`, `expiresAt`, `revokedAt`. For a permanent training grant, `expiresAt` is null; for a subscription, `trainingId` is null and expiration is set. Dates use UTC instants. The library deliberately includes revoked/historical training metadata for continuity; presence of a training or grant alone does not mean it can currently be read. Use `readableTrainingIds` for read actions.

## Payment states, trust, idempotency, and recovery

`PENDING`: no trusted local completion. `PAID`: trusted successful verification and entitlement committed atomically. Provider values: `MOCK`, `SANDBOX`, `ZARINPAL`. Event actions: `REQUEST` (successful authority persistence, code 100), `VERIFY` (provider result). An HTTP/parsing failure can leave no VERIFY event because there is no trustworthy provider result.

The existing Zarinpal contract verifies the stored authority and amount with the configured merchant over HTTPS. There is no signed callback requirement in the consulted REST sample: this implementation treats the callback as an untrusted trigger, not as proof. Never add a fabricated signature requirement or accept callback `amount`, `reference`, user IDs, or `Status=OK` as settlement evidence. Provider card fields are ignored.

Successful code 100 and already-verified code 101 require a usable positive reference. Code 101 permits recovery when Zarinpal verification succeeded before the local database commit. Repeated paid callbacks/verifications do not create another entitlement or extend a subscription. The unique `(Provider, Reference)` index rejects a reference reused for another order; transaction rollback prevents a partial paid order/grant.

The authority creation lock lasts through the provider request (existing HTTP timeout: 15 seconds). This trades a bounded per-user lock for preventing overlapping provider initiation. There is no application guarantee of distributed exactly-once external requests: a process crash or lost provider response after provider acceptance but before authority persistence can leave an orphan authority. The existing provider request has no established idempotency contract. Such an authority is never returned as a checkout link; no code assumes `metadata.order_id` supplies provider-side idempotency. Recovery uses the same request key. Provider reconciliation is not automated.

Required frontend behavior:

1. Generate a UUID `requestId` once per intentional purchase and persist it with product and account before calling checkout. Keep it across double clicks, timeouts, page reloads, and retry UI. A new key represents another intentional purchase, including another subscription renewal.
2. Submit only the product and request IDs. Display catalog `priceRial` and returned/saved `amountRial` with explicit units. A checkout retry honors the old snapshot if catalog pricing changed.
3. Follow the server's `redirectUrl`; do not construct provider URLs or pass a frontend-selected callback.
4. On the result page, authenticate, find the order in `/me/orders`, call owner verification, then refresh `/me/orders` and `/me/library`. Grant UI access only from the server result/current library, never from navigation, callback parameters, local storage, or payment-page text.
5. On `paid:false`, show an unconfirmed/pending outcome with an explicit retry/check-status action. It cannot distinguish cancellation from provider rejection. Avoid a false terminal failure message, new automatic purchases, and endless polling.
6. To resume after interrupted initiation, reuse `productId` + `requestId` from owner history. A stored authority is reused. Another user's order ID gives 404.
7. Combine public catalog with authenticated library for ownership/eligibility: permanent non-revoked training ownership disables repurchase; subscription access can coexist with permanent purchase. Show renewals as explicit purchases. An absent catalog product cannot be newly bought; backend remains authoritative for races.
8. Use `hasSubscription` for current status, entitlement intervals for expiration/upcoming stacked coverage, and `readableTrainingIds` for lesson access. Do not treat the latest paid subscription as necessarily already started.
9. Refresh access after expiration, revocation, or a 403. Preserve private notes when paid access ends.

## Entitlement policy (unchanged)

- Training access requires a published training, a non-revoked grant whose start has arrived, and no expiration or an expiration strictly later than now. A matching permanent TRAINING grant or a subscription covering a training marked `includedInSubscription` is sufficient.
- Subscription is current only while `startsAt <= now < expiresAt`, with no revocation. It also unlocks simulator practice through the existing subscription check.
- New subscriptions start at the later of now and the latest non-revoked subscription expiration. They extend coverage consecutively, including already queued renewals. They do not auto-renew.
- Permanent ownership survives subscription expiration. Subscription-only users lose protected reading/editing on expiration. Revoking one grant does not invalidate another valid path to access.
- Revoked grants remain historical records. Replaying a paid order does not recreate or un-revoke a grant. No historical purchases were modified by this phase.

## Commerce administrator API

All these endpoints require CentAdmin. Operator changes must use server validation; no client role field confers administrator privileges.

| Method/path | Purpose and contract |
| --- | --- |
| `GET /admin/overview` | Counts users/trainings/paid orders, `incomeRial` from paid `ZARINPAL` orders only, and storage bytes. Paid-order count includes sandbox/mock. Not a refund/net-settlement report. |
| `GET /admin/trainings` | `{ trainings, documents, products }`, including inactive/unpublished rows. |
| `PUT /admin/trainings/{id}` | `{ title, description, includedInSubscription, published }`; creates/updates, returns training. Publication and subscription-inclusion flags affect current read access. |
| `PUT /admin/products/{id}` | `{ title, kind, trainingId, priceRial, durationDays, active }`; creates/updates, returns product. Kind/training cannot change after an order exists. |
| `GET /admin/orders` | Latest 500 full order entities, including authority, original snapshot, and owner IDs. No public counterpart exposes all users. |
| `POST /admin/entitlements/{id}/revoke` | No body required, 204 on revocation; 404 if missing/already revoked. Writes access audit. Does not refund or change PAID status. |
| `POST /admin/documents` | Create draft `{ trainingId, title, pageCount, sortOrder }`; existing page upload/publish APIs then control protected content availability. |

Example product request (existing contract):

```json
{"title":"Monthly","kind":"SUBSCRIPTION","trainingId":null,"priceRial":100000,"durationDays":30,"active":true}
```

For TRAINING, supply an existing `trainingId` and null `durationDays`. Product/training saves and entitlement revocation write Cent administrator audit events. There is no dedicated commerce audit/event browsing, refund, discount, or manual entitlement-issue endpoint.

## API compatibility

Paths, request DTOs, authentication policies, and commercial rules remain compatible. Checkout responses add `amountRial`; history responses add `productId`, `requestId`, `trainingId`, `durationDays`. Existing fields remain intact. Mock checkout now correctly reports its persisted `PAID` status. Invalid gateway payloads receive a stable 502 error instead of an unhandled parsing failure. Real orders retain their provider if environment settings change. Clients should tolerate additive JSON fields.

## Configuration, deployment, rollback

No new setting, package, database model change, migration, or data backfill is required. No production/shared database was accessed or migrated. Existing settings:

```text
Cent__Enabled=true
ConnectionStrings__CentDb=<operator-managed connection; optional fallback to FlashcardsDb>
Cent__ClientBaseUrl=https://itatest.ir
Cent__PublicApiBaseUrl=https://<public-api-host>
Cent__AllowedOrigins__0=https://itatest.ir
Cent__Payments__MerchantId=<operator-managed Zarinpal merchant UUID>
Cent__Payments__Sandbox=true
```

`PublicApiBaseUrl` excludes `/api/cent/v1`; `ClientBaseUrl` includes any frontend mount path. HTTPS is required except HTTP loopback in Development. Mock requires Development **and** `Cent__Payments__AllowDevelopmentMock=true`; never enable development mode on a public production host. Preserve the merchant account for pending orders: this schema snapshots environment but not merchant credentials. Merchant rotation/account migration needs an operator plan for pending authorities.

Rollback is an application rollback; there is no migration to reverse. Already stored orders/entitlements remain compatible with the previous schema. Rolling back code reintroduces the corrected initiation/status defects. Do not delete Cent purchase data. For module isolation/disable guidance, see [CENT.md](../../CENT.md).

Provider contract sources consulted read-only on the audit date: [official Zarinpal REST request sample](https://github.com/ZarinPal-Lab/Zarinpal-RestAPI-Sample-php/blob/master/Request.php), [official REST verification sample](https://github.com/ZarinPal-Lab/Zarinpal-RestAPI-Sample-php/blob/master/Verification.php), and [official sample showing the already-verified 101 result](https://github.com/ZarinPal-Lab/SampleCode-Python-Flask/blob/master/zarinpal.py). The older SOAP example is used only to corroborate code 101, not its monetary units. The current REST sample uses `api.zarinpal.com` for API calls and `www.zarinpal.com` for StartPay; the existing adapter uses `payment.zarinpal.com` for live calls/redirect and `sandbox.zarinpal.com` for sandbox. Host aliases/connectivity and actual merchant requirements need staging/provider confirmation; existing host behavior was preserved because this audit did not establish it as a defect. The official documentation portal was reachable but its detailed pages were not readable through the research tool. No new callback authentication, provider idempotency, or verification response amount fields were invented.

## Fresh verification and remaining limitations

Tests use disposable SQLite databases and injected HTTP responses; they do not contact a payment provider or use production/shared data. All results below are fresh executions on 2026-11-19:

| Execution | Passed | Failed | Skipped | Reported test duration | Evidence |
| --- | ---: | ---: | ---: | --- | --- |
| Focused new commerce tests | 19 | 0 | 0 | 51s | `TestResults/Phase7A/phase7a-commerce.trx` |
| Full Cent suite against final rebuilt source (includes those 19 cases) | 127 | 0 | 0 | 10m 22s | `TestResults/Phase7A/phase7a-cent-final.trx` |
| Lerona regression suite | 396 | 0 | 3 | 12m 24s | `TestResults/Phase7A/phase7a-lerona.trx` |

The TRX artifacts and execution logs are local ignored test output, not committed source. No code was changed after the successful final Cent build/run.

The three existing mobile cloud-flow checks (`Mobile_backup_logout_and_fresh_install_use_the_real_api_and_stored_snapshot`, `Mobile_scheduling_sm2_survives_offline_edit_and_fresh_install`, `Mobile_scheduling_leitner_survives_offline_edit_and_fresh_install`) skipped because `LERONA_FRONTEND_DIR` was not set. They require the separate real frontend HTTP harness; no skip or test was changed in this phase.

Commands executed from the backend root (local package cache and CLI home were selected with `DOTNET_CLI_HOME=.dotnet` and `NUGET_PACKAGES=.nuget/packages`):

```powershell
dotnet test backend/Cent.Tests/Cent.Tests.csproj --no-restore --filter FullyQualifiedName~CentCommerceTests --logger "trx;LogFileName=phase7a-commerce.trx" --results-directory TestResults/Phase7A
dotnet test backend/Cent.Tests/Cent.Tests.csproj --no-restore --logger "trx;LogFileName=phase7a-cent-final.trx" --results-directory TestResults/Phase7A
dotnet test backend/Flashcards.Tests/Flashcards.Tests.csproj --no-restore --logger "trx;LogFileName=phase7a-lerona.trx" --results-directory TestResults/Phase7A
```

Focused scenarios include authoritative price/duration snapshots after interrupted initiation, unavailable products and unpublished trainings, accurate mock status and retry identifiers, negative verification, spoofed/cancelled/replayed callbacks, successful code-100/code-101 completion, concurrent authority creation and completion, provider/reference reuse, payment/entitlement rollback and recovery, permanent ownership with expired subscription, revocation, and future-start access rejection. Existing Cent tests cover foreign order verification, owner-only protected pages, subscription stacking/expiry, note preservation, production mock prevention, and host isolation.

An initial full-suite attempt was interrupted; an overlapping focused build encountered locked test output files before tests ran. Neither attempt counts as a pass or as a commerce assertion failure. The completed executions and final TRX files are the fresh evidence; historical results in `CENT.md` are not reused. `git diff --check` also passed.

Builds reported existing EF Core 10.0.11/10.0.13 assembly-version conflicts, package-pruning notices, and the unrelated Lerona infrastructure PKCS 9.0.11 advisory warnings (NU1902/NU1903). Dependencies were not changed in this isolated commerce phase.

SQL Server concurrency/locking and real sandbox/provider connectivity remain unverified in this phase. Before enabling paid production traffic, an operator should validate parallel checkout/renewal and interrupted-flow recovery on isolated staging SQL Server, confirm the merchant environment/hostnames, and validate the provider's sandbox contract. No production payment or deployment is authorized by this document.

Deferred capabilities require separate product approval: discounts, carts, document-level pricing, terminal order statuses, automatic renewal, refunds/reversals, reconciliation jobs, merchant-account snapshots, and history pagination. No approval is needed to review the implemented corrections; Phase 7A stops here for review without commit, push, deployment, or real payments.
