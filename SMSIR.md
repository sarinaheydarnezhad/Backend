# SMS.ir OTP integration

The adapter extends the existing `ISmsOtpSender`; registration/login, OTP storage/expiry/rate limits and JWT/refresh handling are unchanged. No mobile API or navigation change is required.

## Server configuration

`backend/Flashcards.Api/appsettings.json` selects `SmsIr`. Set the following in the backend process/deployment secret store, not mobile public variables or committed JSON:

```powershell
$env:Sms__Provider = 'SmsIr'
$env:Sms__ApiKey = '<rotated-api-key>'
$env:Sms__TemplateId = '<approved-template-id>'
```

The key supplied in chat is deliberately not hardcoded, committed, written to local configuration, logged, or used to make a real request. Rotate that exposed key before deployment. Confirm the actual approved template ID with your SMS.ir account; the sample `123456` is not proof that a template is valid. Its parameter must be named `Code` and support the existing six-digit OTP, including leading zeros. The sample five-digit code does not change the authentication service's six-digit validation/security policy.

`SmsIrOtpSender` sends POST to the fixed SMS.ir verification endpoint with JSON content, `Accept: text/plain` and `x-api-key`. Its body is `{ mobile, templateId, parameters: [{ name: "Code", value: generatedOtp }] }`. Canonical Iranian `+989...` identities remain unchanged in the database/mobile API; only the provider payload drops `+98` to match the supplied ten-digit `9...` contract. Unsupported/non-Iranian recipients return `invalid_phone` without a provider request. This restriction is specific to this sender, not a guessed international-number conversion.

Acceptance requires HTTP success plus a JSON integer `status` equal to 1 and a positive numeric `data.messageId`; HTTP 200 alone is not enough. Provider rejection, malformed responses, network errors and timeouts become the existing safe HTTP 503 `sms_unavailable` error. Caller cancellation propagates. Failed sends are not retried automatically; existing OTP challenge cooldowns remain in effect and unaccepted challenges cannot be verified.

The production HttpClient has a ten-second timeout, redacts the API-key header in HTTP logs and disables redirects so a redirect cannot forward the key to another endpoint. Raw provider messages/bodies, OTP values and keys are not logged or returned to mobile. A positive provider response proves acceptance only, not handset delivery.

To retain the previous gateway instead, set `Sms__Provider=Webhook`, `Sms__Endpoint` to its HTTPS endpoint and `Sms__ApiKey`; it continues using `{ phoneNumber, code }` and bearer authentication. Unknown provider names fail startup rather than silently choosing another transport.

## Verification

`backend/Flashcards.Tests/SmsIrOtpSenderTests.cs` adds request-contract, missing/invalid configuration, unsupported recipient, HTTP/business/malformed response, network/timeout, cancellation and provider-selection tests. The existing phone-auth tests cover unusable challenges after failed delivery.

`dotnet --list-sdks` reports only SDK 8.0.100. The backend and xUnit project target net10.0, so their build/tests and real SQL Server/SMS.ir integration remain unverified. No live request was attempted: real delivery needs a configured valid template/key and an explicitly chosen recipient. Any isolated SDK-8 checks are recorded separately below; they are not a successful backend build or xUnit run.

### Executed checks - October 13, 2026

From `C:\a\LeronaBackend\Backend`:

```powershell
dotnet --list-sdks
rg 'TargetFramework' backend -g '*.csproj'
```

Results: SDK 8.0.100 only; all backend project targets are net10.0. No backend build or xUnit test command was run with the unsupported SDK. The 26 cases in the new xUnit test file remain unexecuted.

An ignored local `.dotnet/smsir-check` harness links the actual sender, existing sender interface/webhook and phone-normalization source into a net8.0 console project. It uses only installed framework references, fake HTTP handlers and a placeholder token type needed to compile phone response records. It does not execute authentication/JWT/database logic, and is not a substitute for the net10.0 test project. No NuGet package sources are enabled in its local `NuGet.Config`.

```powershell
$env:DOTNET_CLI_HOME = Join-Path $PWD '.dotnet\home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; dotnet restore .dotnet/smsir-check/SmsIrCheck.csproj --configfile .dotnet/smsir-check/NuGet.Config
```

First attempt: exit 1 because sandbox access to the user-level NuGet configuration was denied. The same command was rerun with approval: exit 0, local framework-only restore completed. No dependency package installation or SMS request was performed.

```powershell
$env:DOTNET_CLI_HOME = Join-Path $PWD '.dotnet\home'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; dotnet run --project .dotnet/smsir-check/SmsIrCheck.csproj --no-restore
```

Result: exit 0, **25 isolated adapter checks passed**: exact request/leading zeros, seven invalid configurations, four unsupported recipients, nine HTTP/business/JSON failures, network error, timeout, caller cancellation, HTTP registration and normalized phone identity. The checks compiled the actual adapter against .NET 8 and made no network calls. They do not verify full backend compilation, the xUnit provider-selection tests, a real provider response or handset delivery.

`git diff --check` passed with line-ending warnings only. A final PowerShell JSON parse of `backend/Flashcards.Api/appsettings.json` confirmed provider `SmsIr`; there is no API key or template ID in that committed configuration.
