# Lerona password authentication with SMS verification

This is the current policy. It supersedes the historical SMS-only reports.

## User flows

1. **Sign up:** verify the mobile number by SMS, choose/confirm a password, enter
   a name, then create the account. SMS verification alone creates neither a user
   nor a session.
2. **Sign in:** mobile number and password. No SMS or SMS-provider availability is
   required for normal login. Existing password accounts retain their IDs and data.
3. **Forgot password:** verify a recovery SMS for the existing phone, choose a new
   password, preserve the account/profile/data and revoke every old session. The
   reset starts a fresh session.

Google OAuth remains Cent-only. Its separate scheme, endpoints, settings, tables
and frontend are unchanged.

## Endpoints under /api/v1/auth/phone

| Endpoint | Input / result |
| --- | --- |
| POST /login | phoneNumber + password → access/refresh/session tokens |
| POST /otp/request | phoneNumber + purpose (`register` or `reset`) → challenge and deadlines |
| POST /otp/verify | challengeId + six-digit code → registrationToken OR resetPasswordToken and expiry; tokens=null |
| POST /register | phoneNumber + registrationToken + password + confirmPassword + username (profile name) → new account/session |
| POST /password/reset | phoneNumber + resetToken + password + confirmPassword → original account with new password/session |
| POST /register-password | Rejected with 403 phone_verification_required; cannot bypass signup OTP |
| POST /activation/request | Disabled; use the normal password/recovery flows |

Email/Google Lerona routes remain disabled with `phone_password_auth_required`.
Old `authenticate`/OTP-login operations cannot be requested or verified.

## Security and compatibility

- Six cryptographically generated digits; HMAC binds code to operation, challenge,
  canonical phone and recovery user ID. Defaults: two-minute code, five attempts,
  one-minute resend cooldown and five requests/phone/hour.
- Valid OTP yields an opaque 256-bit proof, hashed at rest, valid ten minutes.
  Registration and reset proofs cannot substitute for one another or another phone.
  A replacement challenge invalidates older codes and proofs.
- Conditional writes serialize one-use verification/proof consumption. Signup and
  reset commit proof consumption, account/password changes and token issuance in
  one transaction. Failures roll back all of them.
- Passwords use the existing ASP.NET Identity hasher and 8–128 character policy.
  Login locks the durable user row before password verification/session issuance;
  reset locks that same row before replacing the hash and revoking old sessions.
- Existing password users need no new OTP merely to sign in. No global legacy
  account gate blocks unrelated signup. Local-format historical Iranian numbers
  remain usable. Ambiguous/colliding phone ownership is rejected; accounts never merge.
- JWTs still validate issuer/audience/signature/lifetime and a live owner-bound
  durable session. The password hash must still exist and the user must be active.
  No universal `phone_verified`/`amr=sms_otp` gate remains.
- The two pending additive Lerona migrations retain their committed IDs/columns for
  compatibility, but their Up paths and generated deployment SQL no longer revoke
  historical sessions or rewrite users. The OtpAuthenticated compatibility column
  does not gate password sessions. Neither migration was applied during this task.
- If a different environment already applied the old revoking SQL, changing source
  will not revive those revoked sessions; users can sign in normally with passwords.

## SMS configuration and next deployment

The required server settings are `Sms__Provider=Kavenegar`,
`Kavenegar__ApiKey`, and `Kavenegar__OtpTemplate`. The API supports injected host
variables; an optional ignored SMS-only .env is a local convenience, not required
in the deployed artifact. Host values take precedence. Never put provider keys in
mobile configuration or tracked examples. Delete a real local credential file
only after its exact values have been transferred and verified in the host.

Liara's environment update restarts the current app automatically. The current
live configuration release v48 uses the old v46 code, predates the Kavenegar
adapter and accepts only SmsIr/Webhook.
**Do not set Sms__Provider=Kavenegar on that old live release**: it would fail at
startup and affect both apps in the shared host. Coordinate provider selection
with deployment of the compatible API. Saving only unused Kavenegar credential
keys is distinct from activating the provider, but still restarts the app.

The checkout contains only placeholders in .env.example; targeted current-file
and history review found no real provider key. The user then opened Kavenegar
in Chrome. Its existing key and approved `lerona-login` template were copied
directly in memory to Liara as `Kavenegar__ApiKey` and `Kavenegar__OtpTemplate`.
Exact readback matched; all 17 unrelated values were preserved. No local secret
file was created or found to delete; the placeholder example remains.
Configuration release v48 is READY and both apps' status endpoints are healthy.
Provider selection is unchanged. No SMS or code/database deployment occurred.

Kavenegar's read-only account/config check returned status 430, account identity
verification incomplete (`حساب کاربری احراز هویت نشده است`). The account owner
must complete its verification process before real OTP delivery can work.
No identity documents were submitted and no provider settings were changed.

Cent Google checks passed (44 backend, 61 frontend and 9 synthetic browser
scenarios). The user subsequently requested removing Google from Liara; its two
Enabled/RedirectUri entries were removed and Google remains disabled. Cent source
was unchanged. Synthetic tests are not evidence of a live Google login.

Current Lerona verification results are recorded in the workspace handoff report.
