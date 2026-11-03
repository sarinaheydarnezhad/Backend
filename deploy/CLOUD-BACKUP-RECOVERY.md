# Production backup recovery

On November 3, 2026, release **v44 is READY** with the existing 1 GB `backup-keys` disk mounted at `/data/backup-keys`. The live mount and API health were verified. Myket remains enabled with its verification credential present.

The connected Samsung Galaxy A73, running Android build 3, reproduced a real server failure. Release v43 contained the SM-2 implementation and the database already contained migration `20261102105010_AddSm2ReviewSystem`, but that release had `disks: []`. The backend logged `backup_storage_failed (CryptographicException)` and Data Protection key-ring errors. Requests did reach the backend. No additional schema change was needed.

Deployment through `Deploy-Backend.ps1` restored the existing disk without replacing its keys. The phone then confirmed backup creation. Read-only verification found completed encrypted backups at sync version 25. The latest snapshot decrypted in a separate server process and contained two SM-2 decks and three scheduler states. Verification printed counts only; no plaintext backup was saved or displayed. No migration, restore, sign-out, or local data clearing was performed.

During retry, the app could briefly show an old failed-backup notice while the new operation was running. The mobile backup card now suppresses that historical failure modal while busy; English and Persian regressions reproduced the issue before the fix and pass afterward. Signed Myket build 4 was then installed in place on the same phone; Backup Now confirmed success on its first attempt.

## Deploy future releases

From this backend repository root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File deploy/Deploy-Backend.ps1
```

The wrapper validates a local publish, stages the four source projects, explicitly passes `--disks backup-keys:/data/backup-keys`, and verifies the live mount before reporting success. Temporary build and source packages live in ignored `.dotnet/` directories. It does not apply database migrations.

Liara disk bindings belong to each release. Verify the binding on every source, image, dashboard, or environment-update release. A configured `Backups:KeyRingPath` alone cannot attach a disk. Environment updates inherit the current release's mounts, so check the current binding before changing settings. The same omission previously caused failures in v37, v39/v40, and v43.

For a manually staged source release, keep the binding explicit:

```powershell
liara deploy -a lerona-api --path .dotnet/cloud-backup-source-<id> --disks backup-keys:/data/backup-keys --no-app-logs
liara shell -a lerona-api -c 'mountpoint /data/backup-keys'
```

The mount check must print `/data/backup-keys is a mountpoint`. Then retry Backup Now in the signed-in app and confirm the successful timestamp. Retain the `Lerona.Backups` application name and existing key files. Do not recreate this disk, replace its key ring, or use an ephemeral directory; retained encrypted SQL backups depend on those keys. Existing authentication, SQL and Myket environment settings must be retained.

The earlier v38/v41 repairs also verified persistent encryption keys. The disk was detached by later releases, not deleted. Backups retain their existing 30-day retention, 10 MiB maximum snapshot size and 20 stored-artifact account limit.
