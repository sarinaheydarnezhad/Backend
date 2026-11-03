# SM-2 cloud backup fix

Saving an SM-2 deck previously caused the mobile cloud client to reject its deck operation. Backup requires every operation to synchronize, and the server stored only Leitner scheduling data.

The app and API now synchronize the deck review system, SM-2 repetitions, interval, ease factor, and immutable review events. Backups retain these fields, restore preserves them, and review replay uses the deck's scheduler. Legacy backups without scheduler fields restore as Leitner.

Deploy the backend with the additive EF migration `20261102105010_AddSm2ReviewSystem` before distributing the updated app. The migration adds three columns without changing existing learning data. The idempotent SQL upgrade is in `deploy/sm2-backup-upgrade.sql`. Use the established database migration/deployment process. Production verification and deployment are recorded below.

The app's SQLite migration 7 updates sync triggers, adds missing scheduler metadata to unsynchronized SM-2 operations, and retries rejected SM-2 operations. Local review progress and immutable event rows are preserved. Already acknowledged changes are left intact.

Guest-to-account transfer remains governed by its existing Leitner-only transfer flow. This fix covers signed-in account sync and cloud backup.

Regression coverage includes unreviewed and reviewed SM-2 backups, restore and continued review, fresh-device replay, existing rejected operations, scheduler mismatches, and legacy backups.

Validation: 242 mobile sync/database tests passed; 68 backend backup tests passed after the final restore changes. The final full backend run passed 271 tests with one skipped (272 total), including the legacy backup and mismatched-scheduler restore regressions. TypeScript, ESLint for changed mobile files, formatting, and diff checks passed. The full mobile run passed 1,205 tests and failed eight navigation/card UI tests; sampled navigation and alignment failures also reproduce with the original sync implementation.

Production rollout verified November 3, 2026: a read-only live check confirmed migration `20261102105010_AddSm2ReviewSystem` was applied. Release v43 included the SM-2 code but omitted the backup-key disk. Release v44 restored that existing mount through `deploy/Deploy-Backend.ps1`, with API health and Myket configuration verified. The connected phone confirmed backup creation, including a successful first attempt after installing signed Myket build 4 with the stale failure-modal fix. A read-only server check decrypted the latest completed snapshot at sync version 25 and confirmed two SM-2 decks and three scheduler states. No database migration or restore was executed during this repair. See `deploy/CLOUD-BACKUP-RECOVERY.md` for deployment steps and the separate stale failure-modal fix.
