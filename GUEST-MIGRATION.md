# Guest-copy backend coordination

Guest data remains on the device until explicit user confirmation. The mobile transfer reuses authenticated `/api/v1/sync/push` and `/api/v1/sync/pull`, not a separate migration API. Every source identity is mapped once to a new UUID per account in a durable local journal. Existing server receipts, versions, immutable history, ownership checks and transactions continue to apply.

`GET /api/v1/me/cards/{cardId}/review-state` now additionally returns `totalSuccesses`, `consecutiveSuccesses` and `lastReviewedAtUtc`, alongside its existing fields. This is an additive response change used for authoritative transfer verification. Foreign-owned cards still return 404. Deploy this response update before claiming a mobile transfer is verified.

Only active Leitner decks/cards and compatible complete event histories are copied. Mutable final-state seeds are not uploaded for reviewed cards. Separate card IDs are not collapsed because their text matches. Same-name decks never merge or replace each other implicitly. All authenticated accounts still have the existing free limits: one active deck and fifty active cards. Nonempty accounts cannot add another guest deck until trusted entitlement support is implemented. Requests that exceed those limits fail in the existing transaction; no client override is accepted.

The entire multi-operation transfer is not server-atomic. Each existing push is transactional and retry-safe, and the mobile journal preserves stable identities through partial failure. The client retains guest originals indefinitely and verifies acknowledgements, pull cursors, owned inventory and review state before declaring completion. Concurrent account edits or unresolved conflicts stop verification rather than overwrite data.

Existing rows absent from the sync feed require an authoritative server backfill. The client detects inventory/feed gaps and blocks transfer. Other scheduler metadata, missing histories, study sessions and archived data are not supported by this copy flow. No backend entity or EF migration is necessary or added for this task.

## Verification in this environment

Executed `dotnet --list-sdks`: only **8.0.100** is installed. Executed `rg -n '<TargetFramework>' backend -g '*.csproj'`: all five backend projects target **net10.0**. Backend build and tests were **not executed**, because the installed SDK cannot support that target. The new `SyncTests` covering historical copy/retries, review-state reads, ownership isolation, identical-text card IDs and same-name quota safety are authored but **unverified**. Live SQL Server, EF migration application and phone/device integration are also unverified. `git diff --check` passed (line-ending conversion warnings only).

With .NET 10 installed, run these next; they were **not** executed here:

```powershell
dotnet test backend/Flashcards.Tests/Flashcards.Tests.csproj --filter FullyQualifiedName~SyncTests
dotnet build backend/Flashcards.Api/Flashcards.Api.csproj
```
