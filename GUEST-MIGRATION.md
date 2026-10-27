# Guest-copy backend coordination

Guest data remains on the device until explicit user confirmation. Existing clients can continue using authenticated `/api/v1/sync/push` and `/api/v1/sync/pull`. For an all-or-nothing transfer, updated clients must use the additive `POST /api/v1/sync/guest-migration` endpoint described in `SYNC-RECOVERY-FIX.md`. It accepts the same `{ changes }` schema and returns the same `{ cursor, changes }` acknowledgement schema, but commits the complete bounded transfer in one transaction. Every source identity must still be mapped once to a new UUID per account in a durable local journal. Server receipts, immutable history, ownership checks and entitlement limits continue to apply.

`GET /api/v1/me/cards/{cardId}/review-state` now additionally returns `totalSuccesses`, `consecutiveSuccesses` and `lastReviewedAtUtc`, alongside its existing fields. This is an additive response change used for authoritative transfer verification. Foreign-owned cards still return 404. Deploy this response update before claiming a mobile transfer is verified.

Only active Leitner decks/cards and compatible complete event histories are copied. Mutable final-state seeds are not uploaded for reviewed cards. Separate card IDs are not collapsed because their text matches. Same-name decks never merge or replace each other implicitly. Free accounts have limits of one active deck and fifty active cards; verified active Plus entitlements remove those limits. A nonempty free account cannot add another active guest deck. Requests exceeding applicable limits roll back completely; no client override is accepted.

Legacy multi-push transfers remain non-atomic across requests. The new guest-migration endpoint accepts 1–10000 changes in at most 10485760 bytes, with dependencies ordered before children. The entire request is validated before writes, then applied with the existing per-user lock, transaction and duplicate receipts. It only accepts new deck/card identities, complete compatible ordered review events, or unreviewed states. Settings, archival and updates to existing account entities are not guest-copy operations. Interrupted or uncertain responses require replaying the complete original request, including its IDs, timestamps and versions. Do not split an oversized import into multiple requests and describe it as atomic.

The client retains guest originals and verifies acknowledgements, pull cursors, owned inventory and review state before declaring completion. Concurrent account edits or unresolved conflicts stop verification rather than overwrite data. No backend change can make already-deployed multi-request mobile transfers atomic without client integration.

An untouched legacy account with head zero is bootstrapped from owned authoritative database rows under the same lock and transaction. Only synchronization versions and append-only feed rows are written; learning contents and timestamps are preserved. Saved review progress must agree with deterministic historical replay before bootstrap proceeds. Advanced heads with missing feed versions, inconsistent progress, or contradictory versions return 409 `sync_history_incomplete`; they are never silently reset or reconstructed from guessed data. Other scheduler metadata, missing histories, study sessions and archived guest data remain unsupported. No backend entity or EF migration is added.

## Verification in this environment

The earlier SDK-8 limitation is historical. This repair uses installed SDK 10.0.401 and isolated synthetic SQLite databases. Actual baseline and final commands/results are recorded in `SYNC-RECOVERY-FIX.md`. Live SQL Server locking, Liara configuration and physical-device integration remain unverified.

Focused verification commands:

```powershell
dotnet test backend/Flashcards.Tests/Flashcards.Tests.csproj --filter FullyQualifiedName~SyncTests
dotnet build backend/Flashcards.Api/Flashcards.Api.csproj
```
