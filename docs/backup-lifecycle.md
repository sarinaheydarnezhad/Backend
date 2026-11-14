# Transactional backup lifecycle

Database rows contain both metadata and the encrypted payload. Stored statuses are Pending,
Completed and Failed; Expired is a time-derived metadata presentation, not a stored lifecycle state.
Every retained row consumes the existing configured MaxStoredBackupsPerUser capacity. This setting
and all subscription/entitlement checks remain unchanged.

Creation and finalization occur in one serializable transaction under the existing owner SyncHead
write lock. Pending is not committed before finalization. Failure recording follows rollback in a
separate bounded-time serializable transaction under that same lock. It preserves a concurrently
completed request and does not insert a failure reservation when capacity is already full. Failed
rows have no payload under the existing completion constraint. Completed ciphertext and metadata
commit atomically; no object store or external deletion lifecycle exists.

For a new request UUID, creation selects at most 128 owner-owned Failed rows with ExpiresAtUtc <=
the captured current UTC instant, ordered by expiry and ID, then deletes those rows under the same
transaction. The DELETE repeats owner/status/expiry predicates. It never reclaims Pending, even
when old, and never deletes Completed, including expired Completed rows. Expiry alone cannot prove
an unknown pending operation abandoned; completed retention/physical deletion policy needs an
explicit owner decision. This conservatism can leave capacity blocked by those retained records.

Cleanup and a successful new reservation/finalization commit together. Snapshot/protection/save/
cancellation failures roll cleanup back; failure metadata is a separate best-effort write. When a
legacy backlog still fills capacity after one batch, only eligible cleanup commits and creation
returns the existing 409 backup_capacity_reached contract. A later new request makes bounded
progress. Exact request retries are checked before cleanup: existing Completed remains replayable,
unexpired Failed may retry and expired retained requests return 410. Once an expired failed row is
reclaimed its old request tombstone no longer exists; its UUID is not a perpetual reservation.

SQL Server serializable range locking plus the pre-query owner write lock orders cooperating backup
writers/finalizers and existing restore mutations. SQLite has database-file writer serialization;
its tests do not certify SQL Server behavior. Competing work can time out/deadlock and must retry
using existing request semantics. There is no new unbounded retry. Restore cannot read a deleted
Completed row because cleanup never targets Completed, and reads reject Failed before fetching a
payload. No ownership policy or shared lock implementation is changed.

Repeated cleanup is idempotent. Row deletion removes metadata and any row-contained bytes together;
there is no orphan blob. At most 128 IDs and rows are processed per attempt; this bounds mutations,
not SQL Server scan cost or wall time. Existing owner/expiry indexes remain; production-scale query
plans and lock duration require isolated SQL Server verification. No background scan, new endpoint
or migration is introduced. Failure rows cannot grow beyond configured capacity through the new
cooperating writers. Legacy excess rows require repeated bounded attempts; completed/pending policy
is intentionally preserved. No production cleanup was executed.

## Encryption and operations

Persist and securely back up the existing Data Protection key ring at Backups:KeyRingPath, with the
same application discriminator and original required key material across instances/restarts. Key
availability is separate from database backups. Do not delete/revoke/rotate shared keys as cleanup.
Backup purpose binding remains Lerona.Backups / format / owner UUID / backup UUID. Incorrect keys
fail explicitly; missing durable configuration fails closed. Encryption format and backup format 1
are unchanged. Payload bytes are not logged; cleanup logs counts and transaction staging, and error
logs retain IDs/codes/types without contents. A staged cleanup log does not prove a committed delete.

Monitor capacity conflicts, failed backup creation and failure-metadata persistence warnings. Before
any separately approved completed/pending retention operation, preserve encrypted artifacts and key
material, determine restore/evidence requirements, and validate on isolated copies. Expired failed
rows provide operational failure metadata only, not guest history or restore data. Guest databases,
review events, baselines, checkpoints and existing completed backups are outside this cleanup.

No deployment or key changes occur in this phase. Deploy only after SQL Server concurrency and
operational checks; frontend/backend versions retain their existing backup contract. Application
rollback requires no schema rollback. Already reclaimed expired failed metadata is not recreated
by an older binary; completed backups remain readable with the original keys.
