# Lerona SMS setup for signup and password recovery

Normal login is mobile number plus password. New signup verifies SMS before
password/name creation; forgotten-password recovery verifies a separate SMS
before setting the replacement password. See [the current contract and security
report](PHONE_PASSWORD_OTP_REPORT.md). Historical SMS-only reports are superseded.

## Runtime settings

Required host variables:

- `Sms__Provider=Kavenegar`
- `Kavenegar__ApiKey`: actual server-only provider key
- `Kavenegar__OtpTemplate`: the approved Verify/Lookup template name

Optional settings retain defaults: six digits, 120-second code validity, 60-second
resend cooldown, five attempts and five requests/phone/hour. Keys/templates must
not be placeholders. The sender checks provider debug mode and fails closed on
configuration, permission, transport or delivery errors.

The optional ignored `Backend/.env` can supply local SMS-only settings. Host
environment values take precedence. The API does not need this file after the
actual settings are stored in the hosting environment. Never put these secrets
in the app, an Expo public variable, tracked examples, logs or chat.

From Backend, this checker reads configuration and optionally queries provider
configuration without sending SMS or opening a database:

```powershell
dotnet run --no-restore --project backend/Lerona.SmsCheck/Lerona.SmsCheck.csproj -- --provider-check
```

It prints booleans only. Configuration success does not prove handset delivery.
A real delivery test requires the actual key/template and an approved destination.

## Liara / planned deployment

Liara updates environment variables by restarting the current release. The
currently live v46 source supports SmsIr/Webhook and predates Kavenegar.
**Do not activate Sms__Provider=Kavenegar on that release.** Coordinate provider
selection with deployment of the compatible API. Preserve Cent, JWT, database,
backup, payment and all other environment entries exactly.

Transfer only actual credential values, verify them by reading host settings back
without printing them, then remove the exact local credential file. Do not delete
a placeholder example as if it contained a real key. No real SMS file was present
in this checkout; the example contains placeholders.

The pending Lerona migrations add verification/recovery fields and a compatibility
session column. Their revised Up paths preserve existing users and password
sessions. Review SQL and take the usual database backup before the separately
authorized deployment; no migration or deployment was performed here. If another
environment already applied an older revocation script, its revoked sessions stay
revoked and users can log in again with their existing passwords.

Provider documentation: [Verify/Lookup](https://kavenegar.com/rest.html#verifylookup).
