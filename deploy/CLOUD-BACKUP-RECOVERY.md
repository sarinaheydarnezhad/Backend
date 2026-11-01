Current recovery, November 1, 2026: **v41 is READY** with the existing 1 GB `backup-keys` disk mounted read/write at `/data/backup-keys`. The user confirmed backups worked after the earlier repair. Release v39 subsequently omitted the disk binding; the Myket environment-update release v40 inherited `disks: []`. Live application logs contained `backup_storage_failed (CryptographicException)` from `BackupService` and key-ring errors from Data Protection. The key disk still existed; it was detached, not deleted.

The v41 source deployment explicitly restored the existing binding and retained all environment settings, including enabled Myket billing. Existing key files were present before verification. Separate production processes successfully encrypted and decrypted synthetic data on that disk; no user backup records were opened, no database changes were made, and no existing keys were replaced. Public API health and the Myket catalog were verified after deployment. A retry from the user's authenticated app session remains the final user-flow check. The focused backup/Myket suite passed 87 tests with one optional frontend integration test skipped; an additional 25 focused key-persistence, backup-error and Myket checks passed.

The same problem previously occurred in v37 and was repaired in v38. Disk bindings must be supplied on every release. Both repository-root and `backend/` source deployment folders include `liara.json` with the required binding. For future deployments from this repository root, run `powershell -NoProfile -ExecutionPolicy Bypass -File deploy/Deploy-Backend.ps1`; it prepares the source package, explicitly passes `--disks backup-keys:/data/backup-keys`, and verifies the live mount before reporting success. Staging uses a unique compilation output directory to avoid permissions on existing build artifacts. Environment updates inherit the current release's mounts, so verify them before and after changing settings. Do not recreate the existing disk or replace its key files.

Before the repair, read-only Liara inspection found no `Backups__KeyRingPath` environment variable on `lerona-api`, and `/proc/mounts` showed no application data disk. Live `BackupService` logs identified `/api/v1/backups/` requests rejected for invalid durable key-ring configuration at 14:23:39 and 14:25:34 (timestamps as reported by Liara). The application intentionally returns HTTP 503 `backups_unavailable` before snapshot creation. Existing SQL-backed sync does not require this key ring.

The reviewable configuration is [liara.backups.json](liara.backups.json), paired with `Backups:KeyRingPath` in the API's production settings. It proposes a **new** disk named `backup-keys`, mounted at `/data/backup-keys`; this is not a claim that such a mount already exists. Existing retention (30 days), maximum snapshot size (10 MiB) and per-account stored-backup limit (20) remain unchanged. No ephemeral-path fallback or encryption bypass is added. Existing authentication and SQL environment variables must remain intact.

The follow-up `Read-only file system: '/data'` error confirms that a configured path alone is insufficient. Read-only hosting inspection again found **zero provisioned disks** and no `/data/backup-keys` mount. Data Protection is trying to create its directory on the read-only container root; its cryptographic wrapper then reports `backup_storage_failed`. The usual `backend/publish/liara.json` previously had no disk binding, although the alternate staging descriptor did. Both `backend/publish/liara.json` and its production settings are now paired with the same key path/mount, so deploying the usual publish directory also requests the disk. Disk provisioning and an approved deployment are still required; editing those local files does not create or mount a live disk.

The disk creation and source deployment described below were executed after the user's approval. These steps remain reference instructions for future changes. Creating another disk may incur a hosting charge; deployment and environment changes restart the application and require approval.

1. In Liara, check whether an existing durable key disk or historical encryption keys must be recovered. Retain the `Lerona.Backups` application name and all existing keys. Do not replace a previously used key ring. If there is no previous key disk, provision `backup-keys` with the agreed size:

   ```powershell
   liara disk create -a lerona-api -n backup-keys -s 1
   ```

2. Run `powershell -File deploy/Stage-CloudBackupRelease.ps1` to validate a local publish and prepare a fresh ignored **source** staging directory. The native Liara .NET builder requires `.csproj` files; a binaries-only publish folder fails with `Could not find a .csproj file`. The source package includes all four application projects, retains existing deployment settings, merges the missing production key-ring setting, and stages `liara.backups.json` as `liara.json`. It refuses to replace a different retained key-ring path. Review any additional settings or mount bindings added since this inspection. Deploy the printed source staging directory through the usual Liara release flow only after approval. Mount the disk using the prepared configuration.

3. Ensure the staged production settings contain `Backups:KeyRingPath = /data/backup-keys`; the new source settings supply it. Existing packaging scripts preserve old published configuration, so explicitly merge this key into that retained configuration when staging. The staged release prepared by this task already contains that merge. If configuration is instead managed exclusively through environment variables, add the equivalent variable through Liara after confirming the durable mount is writable. The installed CLI retains existing variables when adding one. This alternative command restarts the app:

   ```powershell
   liara env set -a lerona-api Backups__KeyRingPath=/data/backup-keys
   ```

4. Use an approved test account to run Backup Now, download/read back the stored backup, then run Back Up and Log Out. Confirm a new successful timestamp, encrypted SQL payload, and successful re-login to a fresh local install. Restart the app service and confirm the previously created snapshot is still decryptable. Do not use real user records as test data without authorization.

For deployment of the source staging directory printed by the script after disk provisioning and approval, the explicit binding can also be supplied with the installed CLI (replace the sample directory suffix with the actual printed path):

```powershell
liara deploy -a lerona-api --path .dotnet/cloud-backup-source-<id> --disks backup-keys:/data/backup-keys --no-app-logs
```

After deployment, read `/proc/mounts` in the running app and verify a writable mount at exactly `/data/backup-keys`. Keep the container root read-only; writing keys into `/tmp` or merely making the root writable would not establish durable key retention.

Changing this environment variable alone without a durable writable mount is insufficient. The mounted keys must survive release replacement and be recoverable with the protected SQL backup payloads. Do not provision a new disk or key ring over an existing one, delete retained artifacts, relax backup success validation, or claim the production issue is resolved before these checks.

Initial account connection uses `/auth`, `/me`, and `/sync`; it does not call backup download/restore. Its fresh-device defaults conflict and archived-snapshot application fixes are separate source changes. The standalone backup restore endpoints do require the same durable keys as snapshot creation.
