# Scheduling synchronization contract v2

Scope: Phase 01 (F01, F03, F05, F11). Both schedulers retain their existing interval algorithms.

## Events and dates

Review event IDs are immutable identities. Equal IDs with different content are conflicts.
Keep the original UTC instant. New application events carry
`reviewCalendar: { reviewDate: "YYYY-MM-DD", utcOffsetMinutes: integer }`.
The offset is minutes east of UTC at that instant, in [-840,840].
Adding the offset to the UTC instant must produce reviewDate. Capture the calendar once per review.
An offset is sufficient to reproduce the day; replay must never consult today's device timezone.
Do not synthesize missing historical metadata. Legacy events without metadata use UTC only when reconstruction is necessary.

Confirmed server events are applied by increasing serverVersion; local events follow in stable insertion/outbox order.
Device timestamps are evidence, not an ordering or a maximum-time clamp.
Duplicate delivery never adds a transition. Backwards clock corrections are legitimate receipt-ordered transitions.

## Baselines and projections

The immutable event stream may be incomplete. Accepted historical progress is not a zero-state projection.
A pull response includes reviewCheckpoints for cards referenced by that page:
`{ cardId, throughVersion, state }`. The per-user SyncHead lock covers feed, watermark and state reads.
The state includes every accepted server operation through throughVersion. The watermark may exceed the current page cursor.
The client retains all raw events, excludes covered server versions from replay, then applies only later/unacknowledged events.
Never add counters from the current mutable projection to an unbounded replay.

Checkpoint identity is (cardId, throughVersion); conflicting contents at one identity are rejected.
An older checkpoint cannot replace a newer checkpoint. A checkpoint that cannot account for known review/success counts
is rejected for reconciliation rather than dropping progress.
SQLite migration 8 captures the existing projection, cursor and exact local event IDs represented in that projection.
This local baseline is only replaced by a proven server checkpoint. Covered local IDs prevent double counting at upgrade.
A legacy local due date with identical counters and last review is retained when it disagrees with the server's ambiguous
UTC-derived day, including repeated higher-watermark pulls. No historical timezone is invented.
A subsequent real review with explicit calendar metadata converges scheduling. Until then backup verification may
correctly refuse to certify differing local/server dates; do not erase local data to resolve that refusal.
A nonzero historical baseline cannot be replaced by a different ordinary reviewState snapshot.
Legacy server bootstrap also retains a one-day date ambiguity when the last replayed event has no calendar metadata
and every other reconstructed field agrees. For already stored future-clock outliers, a valid scheduler projection
with matching event/result counts becomes a checkpoint rather than being rejected because timestamp sorting cannot
recover the original receipt order. Original events remain unchanged; a real subsequent review recovers scheduling.

## Scheduler edits

An ordinary scheduler edit is eligible only if the deck has no retained cards (including archived cards),
review states or immutable history. Locally, pending/failed/conflicting dependent card/event operations also block it.
Physical deletion of history is not an eligibility mechanism. Archived records remain incompatible state.
An omitted reviewSystem means preserve the existing scheduler (or default Leitner for creation).
Explicit "leitner" and "sm2" always survive the wire. A versioned empty deck can change in either direction.
SQLite eligibility and initial-state consistency triggers close the create/edit race.
The backend checks under its existing per-user transaction/head lock. Concurrent losing edits receive 409 with a conflict.
A conflict involving two incompatible histories requires explicit reconciliation, never automatic history conversion.

## Clock validation and recovery

Only NEW incoming reviews/initial baseline instants are compared with server receipt time.
Maximum accepted future skew is five minutes. This tolerates normal small clock drift without accepting year-scale jumps.
There is no lower age limit: months/years offline and delayed historical uploads are legitimate.
Calendar consistency is also validated. Receipt/idempotency checks run before temporal acceptance checks for stored duplicates.
Rejected new events receive 422 review_clock_ahead without persisting an event or changing the server projection.
The local failed operation and original event are retained. Never retry it with a different timestamp under the same identity.

Previously accepted future history remains unchanged. A definitely future last review makes the card available in the
study queue, with an explanation. A real new answer uses its own preserved day, advances existing counters once, and
recovers the schedule without clamping to the old maximum timestamp. The date-only queue diagnoses only more-than-day
future skew; server acceptance still uses five minutes. Backups preserve the original evidence.
Unaccepted failed future events cannot safely be rebased into an accepted server checkpoint; sync stops with a controlled
correction message. The actual historical time cannot be inferred. Preserve a copy of the raw DB/outbox, inspect event IDs,
server receipts and the user's known study time, and obtain an explicit correction decision before any future correction
tool is used. No automatic delete, re-date, fabricated success or real-user repair is supplied by this phase.

## Storage, backup and rollout

SQL Server migration PreserveReviewCalendar adds only nullable ReviewEvents.ReviewCalendarJson.
It neither backfills nor updates immutable records. Its downgrade refuses to drop a column containing calendar evidence.
SQLite v8 is transactional and additive; both guest and account databases use the established migration runner.
No existing database is migrated by repository test commands in this phase.

Backup format 1 remains readable by the new backend; omitted calendar data is null.
New backups retain optional calendar JSON and accepted current projections. Restore emits metadata into the feed;
fresh-install checkpoints preserve any missing historical baseline. Old backends have strict backup readers and must
not be used to restore backups containing the new field. Do not downgrade and discard this metadata.

Deployment order: back up infrastructure/data using normal operations, apply the additive server migration, deploy
backend v2, then upgrade every participating frontend. Do not deploy an application binary requiring the column first.
The new sync engine calls authenticated GET /api/v1/sync/capabilities before pushing/pulling; v2 is required.
Against a legacy backend it stops with 426 scheduling_upgrade_required and keeps its outbox.
Old request formats and queues are accepted by the new backend, with deterministic legacy fallback. An old frontend
retry that previously omitted explicit Leitner is a duplicate only if the exact reconstructed legacy fingerprint
matches and its accepted receipt snapshot actually selected Leitner. If omission preserved SM-2, a new explicit
Leitner selection remains a conflict requiring a new operation/version; it is never silently acknowledged as applied.
An old frontend
still contains its original replay defects: wire compatibility does not promise corrected behavior of old binaries.
All devices participating in deterministic scheduling must upgrade. SQL Server locking/migration execution requires
verification against a dedicated isolated SQL Server before release; SQLite tests are not that verification.

Shared deterministic vectors live in frontend test/fixtures/review-calendar-vectors.json and backend
Flashcards.Tests/SchedulingVectors.json. Tests cover both schedulers, Tehran, offset extremes, DST, leap/month/year
boundaries, baseline coverage/idempotency, clock correction, persistence, guest transfer and backup restoration.

