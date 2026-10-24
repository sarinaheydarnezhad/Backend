# Concurrency and query performance

## Request admission

`RateLimits:ConcurrentRequests` (`RateLimits__ConcurrentRequests` in the environment) defaults to 64 per API process; valid values are 1–1024. This is an initial safety ceiling, not a measured capacity guarantee. The gate runs before authentication, including database-backed session validation. It does not queue requests. Saturation returns HTTP 429, `Retry-After: 5` and `Cache-Control: no-store`; the permit is released on completion or failure. `/health` bypasses admission so readiness is not lost solely because requests are busy.

Existing per-IP authentication and per-account backup-creation limits still apply after authentication, with Retry-After headers on rejection. Session revocation and entitlement authorization remain database-validated; there is no shared authorization cache. Tenant predicates, sync-head transaction locks, idempotency receipts and atomic quota checks are unchanged.

Size the ceiling against SQL connection-pool capacity, database CPU/I/O, memory per backup and request latency. Leave connection headroom for maintenance and other processes. Multiple replicas multiply possible database concurrency: budget the combined total, and enforce shared abuse/traffic limits at the gateway. Configure trusted proxy forwarding there before relying on per-IP quotas. Admission is not a substitute for DDoS protection.

## Query changes

- Entitlement usage now returns both deck and card counts in one SQL command, scoped by user. No entity graphs are materialized. Card usage still counts active cards in archived decks, preserving quota semantics.
- Study authorization uses a single entitlement snapshot instead of separately checking and fetching the same subscription and counts. Service reads take two SQL commands for an active subscription, three for a user without a subscription (excluding session validation).
- Unlimited-plan write/review guards only read the active subscription; they skip unnecessary usage counts. Free-plan counts remain inside the existing serialized write transaction.
- Caught-up sync pulls read only the tenant's sync head. Nonempty feeds retain ordered, bounded pages and reject invalid/ahead cursors.
- Backup list and latest-success metadata remain projections, not payload reads. Covering indexes match their tenant filters and mixed sort directions; encrypted payload bytes are excluded. Existing active-deck/card indexes are reused rather than duplicated.

## Migration rollout

The additive schema migration is `20261024150000_OptimizeBackupReadIndexes`. It replaces the backup-list index and adds a filtered completed-backup metadata index. It changes no stored user content. Index creation/replacement can block writes and consume disk/log space; review the generated SQL and schedule it for a suitable maintenance window. Online/resumable options require a separate DBA-reviewed script appropriate to the SQL Server edition.

From the repository root, generate a reviewable upgrade script:

```powershell
dotnet ef migrations script 20261022100000_AddGoogleAuthentication 20261024150000_OptimizeBackupReadIndexes --idempotent --project backend/Flashcards.Infrastructure --startup-project backend/Flashcards.Api --output backup-read-indexes.sql
```

Apply the reviewed script through the deployment pipeline, not concurrently on every API replica. The migration supports rollback to the preceding migration; rollback also rebuilds the original list index. Application optimizations work before index deployment, but index benefits require the migration. No production database is changed by the code/test run.

## Verification before increasing traffic

1. Run `dotnet test backend/Flashcards.slnx`. Regression tests verify SQL command counts, migration/model agreement, covering-index SQL, idle pulls, quota/expiry behavior and admission recovery.
2. Use a staging **SQL Server**, not SQLite, seeded with realistic tenant cardinalities and backup metadata. Inspect actual plans and logical reads for entitlements, backup list/status and sync pulls; verify tenant seeks, sort avoidance and no payload lookups for metadata. Check index write/storage overhead too.
3. Ramp concurrent users gradually with a mix of idle pulls, multi-page catch-up, study authorization, bounded pushes and backups. Include hot-account concurrent writes, logout/revocation, retries, duplicate requests and token-refresh storms. Verify no lost updates, quota overshoot or cross-tenant reads.
4. Record throughput, p50/p95/p99 latency, 429/5xx, active requests, connection-pool waits, lock/deadlock waits, SQL CPU/I/O and process memory. Confirm saturated instances reject promptly and recover, rather than building unbounded queues. Keep test credentials/payloads out of logs.
5. Repeat with all intended replicas and failover scenarios before choosing the final admission ceiling. Regression tests alone do not establish a production concurrency target.

The mobile client coalesces metadata reads, limits background entitlement refreshes, pauses polling offline/in background, and uses jittered retries respecting Retry-After. See the mobile repository's `docs/performance.md` for exact cache lifetimes and authorization boundaries.
