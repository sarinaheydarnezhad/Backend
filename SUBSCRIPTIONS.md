# Subscription model and server entitlements

Payment update: Myket checkout/verification is now implemented; see `MYKET-PAYMENTS.md`. The read-only baseline and payment-follow-up limitations described below predate this integration. Purchases remain disabled until configured, while Full remains unavailable.

## Catalog and product rules

Inspection found no subscription tables/services or existing payment integration. The existing SyncService enforced the free limits directly; it now reuses one EntitlementService. The current targets remain .NET 10 / EF Core 10 and SQL Server.

`backend/Flashcards.Infrastructure/Subscriptions/PlanCatalog.cs` is the sole production plan/price catalog, separate from user subscription records. Prices are integer **toman**, not rial; there is no client price table or currency conversion. Plus offers 1 month for 70,000 toman, 6 months for 294,000 toman (30% off; 49,000 per month) and 12 months for 420,000 toman (50% off; 35,000 per month). The mobile multi-month buttons show monthly equivalents and derive discounts from these totals and the one-month price. These durations are offers only: no duration is applied, renewed or charged by a read request.

The defined Plus entitlement removes the existing server deck/card creation caps. Free remains 1 non-archived deck and 50 non-archived cards across the owner's decks. Cards in archived decks still count if the cards themselves are not archived. The subsequent access-enforcement task also blocks study and new content when an expired/free account exceeds either cap, while preserving data, viewing, editing and export; see `ACCESS-LIMITS.md`.

Premium is the user-facing name for the reserved `Full` internal/API plan; the identifier is retained for compatibility and no data migration is needed. The mobile card explains that Premium is not available to all users right now and offers no selection or purchase action. The plan has no prices, null feature/limit definitions, `isPurchasable: false` and `unavailableReason: "coming_soon"`. Even a trusted database row with an in-period Active Full subscription grants only free access (`Unavailable` effective status). Supporting future Premium features requires an explicit reviewed implementation, not simply a client request or purchase flag.

No payment provider is selected/integrated. **Purchasing is disabled for Plus as well as Full**: `purchaseSupported` and `isPurchasable` are false; Plus explains `payment_provider_not_configured`. Publishing prices does not imply a working checkout. There is no purchase, activation, cancellation, renewal or administrative grant endpoint, no development bypass, and no payment secret/configuration toggle that creates subscriptions. Tests seed trusted database records to exercise validation; these are not real purchases. Provider references/transaction tables were intentionally not added without a chosen verified purchase integration.

## Data model and lifecycle

Apply `backend/Flashcards.Infrastructure/Persistence/Migrations/20261015123452_AddSubscriptions.cs` using the existing deployment process. It adds only `Subscriptions`; no live tables are rewritten and no existing user is granted a plan. The migration was generated and its SQL inspected, but was **not applied to a live database**.

Fields: server-generated subscription UUID, owning UserId foreign key, Plus/Full plan, Active/Expired/Cancelled stored status, StartsAtUtc, ExpiresAtUtc, CreatedAtUtc and UpdatedAtUtc. Period/audit timestamps map to `datetimeoffset(7)`; tracked inserts/updates normalize periods to UTC and use the existing UTC audit stamping. New entities default to Cancelled, not Active. The user FK is NoAction (no cascading learning-data deletion). Checks require supported plan/status values and expiration strictly after start. There is an owner/creation/ID history index.

The filtered unique index `UX_Subscriptions_User_Active` allows **at most one stored Active row per owner**, stronger than only preventing overlapping effective periods. Future-starting Active rows reserve that slot; expired-but-still-Active rows also reserve it. Multiple terminal history records are allowed. A future verified provider writer must lock/transactionally retire the previous Active row before inserting a replacement, and use a unique verified provider transaction reference for replay protection. This task does not add such a writer or silently reconcile billing records during GET requests. The relational tests demonstrate atomic retirement/insertion and duplicate rejection; live SQL Server concurrency is not claimed verified.

Access uses an injected server TimeProvider and the half-open interval **start <= now < expiration**, plus stored status and catalog support:

- No row: Free, inactive.
- Cancelled: no paid access immediately, regardless of remaining dates. No end-of-period cancellation/grace behavior is implied.
- Explicit Expired, or Active with now >= expiration: Expired, no paid access.
- Active before its start: Scheduled, no paid access.
- Active Plus inside the interval: Plus, unlimited server deck/card creation.
- Active Full inside the interval: Unavailable, free limits.

Expired access does not depend on a cleanup job updating stored status. Responses distinguish `storedStatus` from effective `status`; reads never update, extend or create subscriptions. There is no scheduled expiration mutation process. Adding further plans later requires a catalog/enum/constraint migration and explicitly supported entitlements, not duplicate user-subscription tables.

## APIs and ownership

- **GET `/api/v1/plans`**: public server catalog `{ currency: "Toman", purchaseSupported: false, freeLimits, plans }`. Each plan includes `plan`, `isPurchasable`, `unavailableReason`, nullable `limits`/`features` and `prices: [{ durationMonths, priceToman }]`.
- **GET `/api/v1/me/subscription`**: active-session JWT required; `{ subscription, entitlements }`. Subscription is null when no record exists, otherwise contains its ID/plan/storedStatus/effectiveStatus (field `status`), period and audit timestamps. The stored Active row is selected first, otherwise the latest historical row by creation time/ID.
- **GET `/api/v1/me/entitlements`**: same authentication and effective entitlement object, without subscription audit metadata.

Entitlements contain `currentPlan`, `isActive`, nullable `startsAtUtc`/`expiresAtUtc`, `features: { unlimitedDecks, unlimitedCards }`, `limits: { decks, cards }` (null limits mean unlimited), `isPurchasable`, `purchaseSupported`, `withinFreeTierLimits`, owner-filtered `usage: { decks, cards }`, and server `validatedAtUtc`. Period dates remain available for expired/future subscriptions; `currentPlan` is Free unless paid access is effective. `withinFreeTierLimits` independently reports usage <= 1 deck and <= 50 cards even for Plus users.

All user-specific responses derive the owner exclusively from existing validated JWT claims, require the existing active session checks and return `Cache-Control: no-store`. No owner/plan/status is accepted from a client for access decisions. Query owner spoofing is ignored. There are no ID-addressable foreign-subscription reads. The existing sync validator rejects a `subscription` entity type, so generic sync cannot activate a plan.

Actual authenticated writes (`POST /api/v1/decks` and sync deck/card creates) check EntitlementService **inside the existing serialized per-owner sync transaction**. Both direct and sync writes use the same implementation. Free-tier failures retain the existing 403 `{ error, code }` contract and codes `free_tier_deck_limit` / `free_tier_card_limit`. Duplicate sync receipts remain idempotent, and expiry/failed growth does not delete or invalidate existing learning records. No JWT entitlement claims or client-supplied flags can bypass these reads.

## Mobile and remaining integration

The existing CloudClient/SyncEngine expose typed read-only `planCatalog()`, `subscription()` and `entitlements()` methods using the existing HTTPS origin, JWT/refresh/session-epoch pipeline and account-scope operation guard. Public plan reads do not send credentials. Responses are validated; network/auth failures throw instead of inventing a free/paid result. No metadata is persisted or copied into guest storage, and late user-specific responses after logout are rejected.

No Paywall, purchase or subscription activation UI was added. The subsequent access-enforcement task integrates server entitlements with authenticated local creation/study, imports and guest-copy previews, a bounded provisional offline cache and read-only plan information. Guest limits remain local and unchanged. See `ACCESS-LIMITS.md` for every protected operation, cache/isolation rules and limitations. Expiration never deletes existing data.

No new subscription secrets/configuration are required for read APIs: use the existing SQL Server/JWT settings, apply the migration, and use the existing native mobile HTTPS `EXPO_PUBLIC_API_URL`. Real purchases require a selected provider, verified server-side transaction processing, idempotent atomic lifecycle writes and operational reconciliation. None are represented as implemented. SQL Server migration application/concurrent unique-index behavior and real-device entitlement reads remain unverified.

## Changed files

Backend additions:

- `backend/Flashcards.Domain/Subscription.cs`.
- `backend/Flashcards.Infrastructure/Subscriptions/PlanCatalog.cs`, `EntitlementService.cs`.
- `backend/Flashcards.Api/Endpoints/SubscriptionEndpoints.cs`.
- `backend/Flashcards.Infrastructure/Persistence/Migrations/20261015123452_AddSubscriptions.cs` and `.Designer.cs`.
- `backend/Flashcards.Tests/SubscriptionTests.cs`.
- This document and `SUBSCRIPTION-VERIFICATION.md`.

Backend updates: DbContext/model snapshot (subscription table, UTC stamping), Infrastructure DependencyInjection, SyncService (shared entitlement enforcement), V1Endpoints (route registration), PersistenceTests (schema/script checks), README (documentation link). Existing backup/auth changes and pre-existing launch settings were preserved.

Mobile additions: `src/core/sync/subscription.ts`, `src/core/sync/subscription.test.ts`, `docs/subscriptions.md`. Updates: `src/core/sync/cloud-client.ts`, `src/core/sync/sync-engine.ts`. Existing backup/logout/migration UI files were not changed by this subscription task. Exact verification commands/results are in `SUBSCRIPTION-VERIFICATION.md`.
