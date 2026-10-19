# Subscription access enforcement

## Rules and trust boundaries

This task reuses Task 7's subscription catalog, effective UTC subscription validation, authenticated identity, existing owner-scoped sync transaction and local guest/account databases. No payment provider, purchase/activation endpoint, Full entitlement, Paywall design, SQL Server entity or SQL Server migration is added. Apply the existing AddSubscriptions migration before using these backend services.

Free/guest access allows 1 non-archived deck and 50 non-archived cards across all owned decks. A non-archived card still counts when its parent deck is archived, preserving existing quota semantics. Editing does not consume additional quota. Explicitly archiving unwanted active cards/decks is the existing supported reduction action; nothing is automatically archived or deleted. Active Plus removes both caps. Full, cancelled, expired and not-yet-started subscriptions use free access. The server uses start <= UTC now < expiration, not a client review date or a stale stored Active flag.

If either free limit is exceeded, both additional content creation and study/review are denied, even when the other dimension is below its cap. Exactly 1 deck/50 cards permits study, but not another deck/card. Viewing, statistics, editing, explicit archiving, export, synchronization reads and backup remain available. Existing cards, scheduling state and immutable review events are preserved.

## Protected server operations

| Operation                                  | Enforcement                                                                                                                                                                                                                             |
| ------------------------------------------ | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| POST /api/v1/decks                         | SyncService.CreateDeckAsync -> PushAsync -> EntitlementService.EnsureDeckCreationAllowedAsync.                                                                                                                                          |
| POST /api/v1/sync/push, new deck upsert    | Same shared creation guard, including cross-dimension excess.                                                                                                                                                                           |
| POST /api/v1/sync/push, new card upsert    | EnsureCardCreationAllowedAsync; owner/parent/version checks remain in SyncService.                                                                                                                                                      |
| Bulk sync/guest-copy/import writes         | Each creation/review mutation uses those guards inside the existing per-owner SyncHead lock/transaction. A failing push rolls back the entire push, entity changes, receipts, versions and feed. The existing maximum batch size is 50. |
| POST /api/v1/sync/push, reviewState upsert | EnsureStudyAllowedAsync after validating the owning active card/deck. Includes initial state and progress updates; immutable-event derivation checks are retained.                                                                      |
| POST /api/v1/sync/push, reviewEvent create | Same study guard, using current server entitlements even for backdated/offline events. Ownership, immutable events and deterministic event-state update remain intact. Archived cards/decks cannot accept new review mutations.         |
| POST /api/v1/me/study/authorize            | Authenticated, body-free online preflight; returns the existing Entitlements DTO only after the current server study check. This is not a signed lease or bypass token; every later review mutation is checked again.                   |

Existing already-accepted idempotent receipts may be acknowledged after expiry, but do not write another event or extend access. New identities/receipts cannot replay that privilege. Account-specific identity comes only from the existing validated JWT/session claims. Existing subscription/entitlement GETs, learning GETs and sync pull remain readable; there is no dedicated server-side study-session, review-write, card-create or bulk-import endpoint besides the paths listed above.

Denials use HTTP 403 with stable code free_tier_deck_limit, free_tier_card_limit or free_tier_dataset_limit and JSON { error, code, limits: { decks, cards }, usage: { decks, cards } }. Error explanations are safe; no payment credentials or foreign metadata are included. Read APIs are not made paid-only.

## Protected mobile operations

| Operation                                           | Enforcement                                                                                                                                                                                                                                                                                                                |
| --------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Deck/card editor entry                              | Existing focused-resource loaders call application.assertLearningAccess before showing a creation flow. Editing still works.                                                                                                                                                                                               |
| application.createDeck / createCard                 | Injected LearningAccessController checks current server permissions/usage plus local active usage. SQLite insert/restore guards cover direct repository writes and concurrent growth.                                                                                                                                      |
| CSV/XLSX/APKG import                                | TransferService preflights the complete selected addition count before saving; each individual create also uses the guarded application. Existing partial-import summaries still report per-record storage failures honestly. Server writes use sync guards.                                                               |
| Explicit guest-data transfer                        | Existing preview/confirmation/journal/verification flow now fetches actual server entitlements and uses those limits rather than an unconditional free-only blocker. A verified Plus lease lets staging pass existing SQLite guards. Guest content is never silently uploaded or removed; same-name decks remain distinct. |
| Study queue / all-decks or deck-specific start      | Shared study use cases enforce access; covers Home, Study tab, deck details and notification/reminder entry.                                                                                                                                                                                                               |
| Resume/read current study item and snapshot         | getStudySnapshot/getCurrentStudyItem enforce access, including direct/stale session navigation. Session metadata, progress/history and viewing learning content remain readable.                                                                                                                                           |
| Retry missed cards / review-again                   | Shared reviewMissedCards guard before creating another session.                                                                                                                                                                                                                                                            |
| Submit button/swipe answer / application.reviewCard | Access is checked before preparing/persisting the review. SQLite guards also protect review-event insert, review-state update and study-session insert/continuation. Cancellation remains available without changing completed review history.                                                                             |
| Sync conflict retry / queued imports/reviews        | Server revalidates every unapplied mutation. CloudClient's quota-error callback invalidates local paid access and displays the real safe denial. Failed operations are preserved for the existing explicit retry/conflict flow, not silently discarded.                                                                    |

Application services, transfer services and entitlement controllers are revision/scope-bound. Entitlement reads verify that SecureStore's bound identity matches the selected account before and after the request; this covers the sign-in interval between selecting storage and persisting new credentials. Late requests after account switches, credential changes or a same-account server denial cannot populate another account or resurrect an invalidated paid cache.

## Offline UX and storage

Authenticated operations request current entitlements when online; study preflight uses the new authenticated POST. Requests in flight are deduplicated within the scoped controller. The UI refreshes after sign-in, synchronization pulls, foreground/reconnect events and once per minute while foregrounded. Nested start/answer internals reuse the check already performed for that operation rather than issuing duplicate preflights.

The small local_entitlements cache is installed transactionally beside the existing local free-tier triggers in each scoped SQLite database. It contains only owner-keyed entitlement metadata, request time and a provisional validity deadline; it is not a subscription grant table, a sync payload, a token store or a second server authority. No guest row can unlock Plus. Backend/session/ownership infrastructure is unchanged.

A previously verified Plus result is used offline only provisionally, bounded by the lesser of remaining server-reported subscription time and **5 minutes** from request start. Request latency cannot extend the deadline. Older responses and identical-timestamp replays cannot renew it. Clock rollback before the request time, expiration, invalid/unknown cache or revoked/forbidden responses fall back to free limits or a safe error. Network failures never display server-confirmed access. SQL triggers enforce the same deadline at local persistence. No paid grant survives indefinitely through process restarts.

Guest creation/study never contacts the backend or installs account permissions. Existing guest outbox records remain local: the existing SyncEngine account-scope guard prevents sending them. Only the explicit guest-transfer confirmation stages new account-owned identities.

The shared access notice shows restrictions or provisional status with a View plans action. Settings has the same entry and a minimal read-only server catalog/access/expiration screen. **No Paywall existed**, so this route is plan information only, not a fake checkout or a newly designed purchase flow. Full and real payments remain unavailable. A future Paywall can replace this navigation destination when purchase-provider verification is implemented.

## Remaining risks and limitations

- Offline clients cannot observe immediate cancellation/revocation or other-device growth. A bounded provisional lease is deliberately not a security guarantee. The server rejects newly submitted unauthorized content/progress using current data; local unsynced content/history is retained, not silently changed.
- A rooted/modified client can alter its own SQLite/device clock or render study content it can already view/export. This cannot grant server entitlements; authoritative sync enforcement remains mandatory. Read access/export are intentionally not prohibited.
- Multi-request imports and guest transfers are not one server-wide transaction. Existing retry-safe journals/receipts preserve partial success and report incomplete verification; each individual push is atomic. A subscription expiring mid-transfer may leave recoverable staged/acknowledged data requiring a refreshed subscription or explicit quota resolution before retry.
- Pending rejected review operations are not automatically discarded to satisfy quotas. Use the existing explicit retry/conflict handling after resolving access. The known SQLite-backed legacy review-history test failure is documented separately.
- Real SQL Server deployment/concurrent quota behavior, a physical device/native build, real payment verification and a real Paywall remain unverified/not implemented. No new secrets or dependencies were added. Existing transitive package security advisories remain.

## Files changed for this task

Backend updates:

- backend/Flashcards.Infrastructure/Subscriptions/EntitlementService.cs
- backend/Flashcards.Infrastructure/Sync/SyncService.cs
- backend/Flashcards.Infrastructure/Sync/FreeTierLimitException.cs
- backend/Flashcards.Api/Endpoints/V1Endpoints.cs
- backend/Flashcards.Api/Endpoints/SubscriptionEndpoints.cs
- backend/Flashcards.Tests/SubscriptionTests.cs
- ACCESS-LIMITS.md and ACCESS-LIMITS-VERIFICATION.md
- SUBSCRIPTIONS.md (updated the earlier implementation's now-superseded integration limitations)
- README.md (access-enforcement documentation link)

Mobile additions/updates (only task-specific edits; earlier backup/auth work is preserved):

- lm/src/core/ports/learning-access.ts
- lm/src/core/access/learning-access.ts
- lm/src/core/access/learning-access.test.ts
- lm/src/core/composition/learning-access.ts
- lm/src/core/database/entitlement-cache.ts
- lm/src/core/database/free-tier-guards.ts
- lm/src/core/database/database-error.ts
- lm/src/core/composition/application.ts
- lm/src/core/composition/sync.ts
- lm/src/core/composition/transfer.ts
- lm/src/core/application/create-application.ts
- lm/src/core/application/create-application.test.ts
- lm/src/core/sync/cloud-client.ts
- lm/src/core/sync/cloud-client.test.ts
- lm/src/core/sync/secure-logout.test.ts
- lm/src/core/sync/guest-transfer.ts
- lm/src/core/sync/guest-transfer.test.ts
- lm/src/core/storage/guest-free-tier.test.ts
- lm/src/shared/access/learning-access-notice.tsx
- lm/src/shared/access/learning-access-notice.test.tsx
- lm/src/features/settings/presentation/subscription-screen.tsx
- lm/src/features/settings/presentation/settings-account-card.tsx
- lm/src/features/decks/presentation/deck-editor-screen.tsx
- lm/src/features/decks/presentation/use-cards-view-model.ts
- lm/src/features/study/application/create-study-use-cases.ts
- lm/src/features/study/application/study-queue.test.ts
- lm/src/features/study/application/study-session-flow.test.ts
- lm/src/features/study/application/scheduler-selection.test.ts
- lm/src/features/study/data/scheduler-persistence.test.ts
- lm/src/features/transfer/application/transfer-service.ts
- lm/src/features/transfer/application/transfer-service.test.ts
- lm/src/features/transfer/data/adapters.test.ts
- lm/src/shared/navigation/navigation.test.tsx
- lm/src/shared/localization/messages.ts
- lm/src/app/_layout.tsx
- lm/src/app/settings/subscription.tsx
- lm/src/core/composition/learning-access.test.ts
- lm/src/core/sync/subscription.test.ts
- lm/docs/access-limits.md
- lm/docs/subscriptions.md (updated the earlier integration limitations)

Scheduler/import/navigation unit fixtures that deliberately use multiple decks now inject an explicitly permitted policy so they continue testing those algorithms/navigation rather than accidentally asserting unlimited guest access. Dedicated new controller/SQLite/composition/UI tests exercise real free/Plus/expiry/isolation rules. The existing logout fetch mock received only a type-safe RequestInfo | URL annotation for the project's fetch overloads. No production entitlement bypass was introduced for tests.

Exact validation commands/results are recorded in ACCESS-LIMITS-VERIFICATION.md.
