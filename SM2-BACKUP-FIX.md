# SM-2 cloud backup fix

Saving an SM-2 deck previously caused the mobile cloud client to reject its deck operation. Backup requires every operation to synchronize, and the server stored only Leitner scheduling data.

The app and API now synchronize the deck review system, SM-2 repetitions, interval, ease factor, and immutable review events. Backups retain these fields, restore preserves them, and review replay uses the deck's scheduler. Legacy backups without scheduler fields restore as Leitner.

Deploy the backend with the additive EF migration `20261102105010_AddSm2ReviewSystem` before distributing the updated app. The migration adds three columns without changing existing learning data. The idempotent SQL upgrade is in `deploy/sm2-backup-upgrade.sql`. Use the established database migration/deployment process; no production deployment was performed by this change.

The app's SQLite migration 7 updates sync triggers, adds missing scheduler metadata to unsynchronized SM-2 operations, and retries rejected SM-2 operations. Local review progress and immutable event rows are preserved. Already acknowledged changes are left intact.

Guest-to-account transfer remains governed by its existing Leitner-only transfer flow. This fix covers signed-in account sync and cloud backup.

Regression coverage includes unreviewed and reviewed SM-2 backups, restore and continued review, fresh-device replay, existing rejected operations, scheduler mismatches, and legacy backups.

Validation: 242 mobile sync/database tests passed; 68 backend backup tests passed after the final restore changes. The final full backend run passed 271 tests with one skipped (272 total), including the legacy backup and mismatched-scheduler restore regressions. TypeScript, ESLint for changed mobile files, formatting, and diff checks passed. The full mobile run passed 1,205 tests and failed eight navigation/card UI tests; sampled navigation and alignment failures also reproduce with the original sync implementation.

Production rollout status, November 2, 2026: the backend has been rebuilt and staged; all 271 executable backend tests passed, with one skipped. The owner will apply the database migration. A final read-only live check still listed only the nine earlier migrations, so no backend deployment was started. The existing backup-key disk mount was verified. The Myket APK was rebuilt as version 1.0.0, Android build 3, and its signature, billing integration and all four Vazirmatn font assets passed verification. Complete the migration before deploying the new API.
