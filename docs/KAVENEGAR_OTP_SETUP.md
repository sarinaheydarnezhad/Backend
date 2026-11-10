# Lerona SMS OTP authentication

Lerona registration and login use the same SMS-only flow. Cent has its own routes,
database context, authentication scheme and Google/password services; none change.

1. `POST /api/v1/auth/phone/otp/request` with `{ "phoneNumber": "09121234567" }`.
   The optional `purpose` must be `authenticate`. Local, international Iranian,
   Persian and Arabic digit formats normalize to `+989…`.
2. Kavenegar sends six digits through Verify/Lookup and the configured template.
   The response contains a challenge ID, expiration and resend deadline, never tokens.
3. `POST /api/v1/auth/phone/otp/verify` with `{ "challengeId": "…", "code": "123456" }`.
   A successful response contains `tokens`; legacy proof fields are always null.
   An existing verified phone opens its original user ID. A new phone creates a user
   and sync head only after verification and only when the legacy inventory is resolved.

All old Lerona password/email/Google/signup/activation paths return HTTP 410 with
`sms_otp_only`. No password, email, username, Google token or registration proof is
required. Old registration/activation OTP challenges cannot authenticate.

## Runtime configuration

The actual configuration is in Git-ignored `Backend/.env`. `.env.example` contains
placeholders and safe numeric defaults only. Never commit the runtime file, put
provider secrets into Expo/public variables, or print the file in diagnostics.

The API calls `AddLeronaSmsEnvironment` before service registration. It finds the
repository-root `.env` from the API project directory, or a content-root `.env` in a
published deployment. It parses data without shell execution and imports only
`Sms__Provider`, `Kavenegar__*` and `Otp__*`. Database, JWT, backup and Cent settings
must remain in the host's existing configuration. Precedence is JSON < `.env` <
host environment < command line. A hosted environment can use injected secrets
without a file. Mount `.env` read-only with access limited to the service account.

Required runtime settings: `Sms__Provider=Kavenegar`, `Kavenegar__ApiKey`, and
`Kavenegar__OtpTemplate`. Preserve the API key and approved template exactly.
OTP length is fixed at six. Defaults are 120-second expiration, 60-second resend
cooldown, five verification attempts and five requests per phone per hour.

From `Backend`, this read-only check loads the same SMS configuration as the API
and prints booleans only. It never sends an SMS or opens a database:

```powershell
dotnet run --no-restore --project backend/Lerona.SmsCheck/Lerona.SmsCheck.csproj -- --provider-check
```

The checker must confirm both configured credentials and provider debug mode
disabled before claiming live activation. A failed check alone cannot distinguish a
network, permission, account or provider configuration problem. An additional
read-only HTTPS probe in this run failed with `EACCES` before reaching Kavenegar;
outbound access in the execution environment prevents live verification. Repeat
the checker on an authorized host with outbound HTTPS access. An approved
template and Verify/Lookup permissions require provider confirmation and an
explicitly authorized delivery test. The current run did not confirm a usable
live account. No real SMS has been sent.

The sender uses HTTPS POST to [Kavenegar Verify/Lookup](https://kavenegar.com/rest.html#verifylookup),
with the normalized receptor, six-digit token, template and `type=sms`. It performs
a read-only account/config check before sending and rejects debug mode. It rejects
HTTP/provider failures and malformed responses. Provider URLs, bodies, OTPs and
raw exceptions are excluded from logs/errors; HTTP client loggers are removed
because Kavenegar embeds the key in its URL path. Redirects and simulated fallback
are disabled. Test SMS transports exist only in the test assembly.

## Sessions and deployment

Apply the Lerona-only `RequireVerifiedPhoneAuthentication` migration if not already
applied, then `SmsOtpOnlySessions`. Review generated SQL before an approved rollout.
The latter adds `AuthSessions.OtpAuthenticated`, defaults historical sessions to
false, revokes them, and consumes obsolete OTP operations. JWT validation requires
`amr=sms_otp`, a live OTP-authenticated session and current durable phone ownership.
Refresh rotation also requires this marker. Old sessions cannot become valid again
after a later OTP login. Access and refresh lifetimes, reuse detection, logout and
user-scoped storage remain intact. Cent tables and sessions are untouched.

No migrations have been applied to production and no production deployment has
been performed. Production deployment requires the user's explicit approval.

## Legacy account recovery

Use [the read-only inventory](../deploy/lerona-legacy-phone-inventory.sql) against
the intended Lerona database with a read-only role. Results contain affected user
IDs and reasons; store them only in restricted operational storage. A collision
query includes phone numbers and must never be put in public logs/source control.
No host Lerona connection string was available to the activation checker, so this
run cannot report actual affected account IDs or counts.

Do not infer ownership from old phone strings, email matches, client flags, or an
OTP sent to an unrelated number. A verified OTP against an unresolved account
returns `409 account_recovery_required`, consumes that challenge, issues no tokens
and changes no user data. New signup also returns this recovery result while any
active account has unresolved ownership, preventing an email-only legacy user
from inadvertently getting a duplicate account. Verified existing users can log in.

Proposed staff-assisted migration procedure (not an implemented public endpoint):

1. Review the affected ID, historical ownership evidence and normalization
   collisions. Use independently verified purchase records or a documented
   identity/support process. Do not authenticate through removed password/Google APIs.
2. Obtain explicit authorization to recover that specific existing account.
   Approve a short-lived, single-use recovery authorization bound to user ID,
   canonical phone, staff decision and the recovery operation.
3. A restricted recovery tool must send and verify a fresh Kavenegar OTP bound to
   that authorization, user ID and phone. An ordinary authenticate OTP cannot be
   reused for recovery. No real SMS until destination and delivery approval exist.
4. In one transaction, lock the user and phone reservation, recheck ownership,
   reject conflicting/archived/colliding accounts, consume recovery evidence,
   bind verified ownership to the original user ID, and revoke all old sessions.
   Preserve every existing relationship, subscription, purchase and progress row.
   No merging, deletion or automatic data overwrites.
5. Record the audited decision without OTPs/secrets. The user then signs in through
   the normal SMS flow. If ownership is uncertain, leave the account quarantined
   for further review. Signup stays closed until the unresolved inventory is cleared.

See [the implementation and test report](SMS_OTP_ONLY_REPORT.md) for the final file
manifest, endpoint list, executed tests and remaining activation work.
