# Phase 8 — Cent administration API handoff

Implemented locally on 2026-11-22. Base path: `/api/cent/v1`. Phase 9 is excluded. No production operation, migration application, real payment, commit, push, or deployment.

## Ownership and architecture

Cent sessions, `CentUser.Role`, `CentDbContext`, existing `cent_audit_events`, document page storage, product/order/entitlement services, curriculum import and versioned exam services are reused. `CentAdminSecurity.cs` adds policy definitions; `CentAdminConsole.cs` adds administrative projections. No shared host, Lerona authentication, entities, contexts, migrations, subscriptions, or infrastructure is changed. No new table or migration is necessary. The single existing role column accepts all five role names within its current 16-character limit.

All repositories were clean at entry. Allowed roots: `Backend/backend/Cent.Module`, `Backend/backend/Cent.Preview`, `Backend/backend/Cent.Exam.Tests`, `Backend/backend/Cent.Tests`, `Backend/docs/cent`, and the separate `itatest` repository. `D:\Lerona\Frontend` is protected. Existing compatibility records remain in [Phase 7A commerce handoff](PHASE_7_COMMERCE_API_HANDOFF.md); this handoff supersedes its admin role/list/bootstrap descriptions only.

## Authorization matrix

Every administrative endpoint requires the existing **CentSession** bearer scheme and a server-side policy. Lerona JWTs do not substitute. Session authentication reads the current user role from the database on every request, so role changes apply immediately to existing sessions. Anonymous: 401. Student or unrelated specialist: 403. UI visibility is supplementary.

| Role | Permissions |
| --- | --- |
| SuperAdmin | overview, users, content, exams, finance, roles/reports/audit, settings |
| ADMIN (legacy alias) | Same as SuperAdmin |
| UserManager | overview, user search/detail and purchase/access summaries |
| ContentManager | overview, resource/training/PDF management, curriculum visibility/import |
| ExamManager | overview, question bank and existing version publication/archive/activation |
| FinanceManager | overview including finance, products/offers, orders, subscriptions, entitlement revocation |
| STUDENT | No administrative permission |

Policy names: `CentAdmin` means any recognized administrative role; `CentAdmin:<permission>` enforces the matrix. Every existing admin write endpoint was reassigned to its domain policy, including existing subject commerce and exam endpoints. Shared implementation is not replaced. One role per account is supported; combined specialist roles are intentionally unsupported.

## Read contracts

All paths below are relative to `/api/cent/v1`. All are GET unless stated otherwise. Private/no-store and nosniff are inherited. No password hash, session hash, Google subject, OTP, bootstrap secret, merchant credential, or payment authority is returned by these projections.

| Path | Permission | Contract / bounds |
| --- | --- | --- |
| `/admin/access` | any admin | `{role, permissions[]}` |
| `/admin/overview` | overview | asOf, total users/documents, published documents, active published version flags, submitted attempts; finance object only for FinanceManager/SuperAdmin |
| `/admin/users` | users | `page,size,search,role`; identity summary and verifiedIdentity boolean |
| `/admin/users/{id}` | users | safe user summary, order/paid counts, latest 50 entitlement summaries |
| `/admin/resources` | content | `page,size,search,status=published|draft`; document metadata |
| `/admin/resources/{id}` | content | document, up to 200 sections, 50 related products, direct-training grant count |
| `/admin/trainings` | content | existing editor options, capped at 200 trainings/500 documents; financial product list removed |
| `/admin/products` | finance | up to 200 product records |
| `/admin/product-options` | finance | up to 200 product records and training identity/title options, no document content |
| `/admin/subject-offers` | finance | up to six canonical offers, release and approval metadata |
| `/admin/orders` | finance | `page,size,search,status`; title/email/exact-reference search, financial snapshot |
| `/admin/orders/{id}` | finance | order snapshot, subject lines/access, entitlements, latest 50 sanitized payment events, owner recovery explanation |
| `/admin/subscriptions` | finance | `page,size,search,status=active|expired|upcoming|revoked`; user, plan snapshot, dates, order relationship |
| `/admin/assessments` | exams | up to 100 version metadata records |
| `/admin/assessments/{id}` | exams | immutable definition JSON, status/configuration and attempt count; no content manifest/answer export |
| `/admin/question-bank` | exams | `page,size,search,status=published|draft`; question/options/key/explanation/relationship and managedByRelease flag |
| `/admin/questions/` | exams | compatibility read capped at 100; new UI uses paginated bank |
| `/admin/curriculum` | content | courses, source/chapter/topic/section/exercise counts, latest ten imports; no duplicate CMS |
| `/admin/reports?days=30` | roles | 1–90 UTC calendar days; registration buckets, order status counts, attempts started/submitted by kind |
| `/admin/roles` | roles | supported roles, permission matrix, legacy alias information |
| `/admin/audit` | roles | paginated existing audit records |
| `/admin/settings` | settings | allowlisted existing booleans and storage limit; read-only, no secrets |

Paged envelope: `{items,total,page,pageSize}`; default 20, maximum 50, page clamped to 1–10000, search trimmed/capped at 120 characters. Ordering is stable by date and identity, or subject and identity for questions. Fixed ordering is used consistently; arbitrary client sort expressions are not accepted. Explicit bounds on editor options are not a claim of a fully paginated large-scale content editor.

## Writes and integrity

`PUT /admin/users/{id}/role` accepts `{role,confirm:true}` and requires SuperAdmin. No self-role change; administrative targets need existing verified Google identity; last SuperAdmin cannot be demoted. A serializable transaction commits the role and `ROLE_OK:old>new` audit event together. No account deletion, suspension model, credential editing, or public role-assignment route is added.

Existing writes are reused:

- Content: training PUT, document creation/page upload/publication, draft-only deletion, curriculum import. Existing WebP storage/limits remain. Published pages cannot be overwritten. The UI retains the PDF.js conversion/upload workflow.
- Exams: question PUT and version publish/archive/activate. PUT refuses edits to an existing active or imported question; create a new manual draft/release instead. Historical result snapshots and published definition integrity remain enforced. Import/publication/activation gates are preserved; no approval gate is bypassed.
- Finance: product PUT preserves sold product identity constraints, existing offer approval requires matching release plus explicit reviewed-content acknowledgment, existing recommended bundle validation, existing entitlement revoke. Revoke and audit now commit atomically. No manual-paid, refund, adjustment, or automatic-renewal API exists.

Successful changes use the existing audit table: actor UUID, action, target UUID, UTC timestamp. Role changes encode old/new role and success. Other existing action names imply a committed success. Rejected authenticated administrative domain mutations record `ADMIN_REJECTED_<status>` without body, credentials or payment data. Framework authorization denials are rejected before domain handlers and are not copied to this table. Audit reads are paginated; no raw payload logging, public event-ingestion endpoint, new background logger, or automatic retention deletion is introduced. Long-term audit archival/retention remains an operator policy decision. Existing non-GUID exam audit target limitations remain; version status is independently persisted.

## Reliable metric definitions

- Registered users: all Cent user rows, not Lerona accounts. No invented active-user metric.
- Resources: Cent documents; published means persisted Published flag.
- Available assessment versions: persisted PUBLISHED plus AvailableForNewStarts; operational engine configuration is visible separately under settings. This does not imply external academic certification.
- Submitted participation: Cent attempts with SubmittedAt. Reports group attempts by CreatedAt range; submitted is the subset currently submitted, not submissions made during the range.
- Total/paid/pending orders: current persisted status. Pending is not necessarily a definitive failed transaction.
- Main-gateway receipts: sum of PAID orders with Provider exactly ZARINPAL; mock and sandbox excluded. This is not accounting revenue recognition or net refunds.
- Active legacy subscriptions: Kind SUBSCRIPTION, not revoked, StartsAt <= server UTC now < ExpiresAt. Future and expired intervals are distinct. Subject-access intervals remain available in order details.
- Reports use UTC midnight boundaries, end at the request instant, cap 90 days. Registration chart omits zero rows. SQL Server aggregates registrations by calendar fields in SQL; isolated SQLite tick storage uses bounded daily COUNT queries, never raw account materialization.
- No scientific performance aggregate is invented from incompatible stored result JSON formats.

## Secure initial SuperAdmin provisioning

No real account was created, promoted or modified in this task. The old unauthenticated new-account/password bootstrap was replaced. Do not reuse its former request body.

Local or production procedure, performed only by an authorized operator:

1. The intended person signs in through the existing verified Google flow to create/reuse their Cent identity. Obtain their Cent UUID from their authenticated `GET /me` response. A password-only account has no supported independent verification evidence and cannot be bootstrapped.
2. Verify that exact identity out of band. Generate a fresh cryptographically random secret of at least 32 characters. Through the existing deployment's secure configuration mechanism, temporarily set `Cent:BootstrapUserId` to that UUID and `Cent:BootstrapToken` to the secret (`Cent__...` environment names are equivalent). Do not commit either value or put secrets in command history, URLs, screenshots or reports. Local Cent.Preview accepts the same configuration; production uses the existing Cent-enabled host with HTTPS. This document does not authorize changing production configuration or assigning any real account.
3. From the intended account's existing Cent authenticated session, send `POST /api/cent/v1/auth/bootstrap`, `Authorization: Bearer <existing-session>`, `X-Cent-Bootstrap: <operator-secret>`, and `{}`. The operator must supply the secret privately. The authenticated UUID must match configuration and have a verified Google subject. No password is created and no additional session token is returned.
4. The serializable transaction rejects if any ADMIN/SuperAdmin already exists. Success returns only `{id,role}` and records `BOOTSTRAP_SUPERADMIN_OK`. Reload the existing authenticated session/UI.
5. Immediately remove both bootstrap configuration values and restart/reload configuration using existing deployment procedures. Repeated calls after initial provisioning are rejected. Existing SuperAdmins use the audited role-management API for additional verified administrators. Losing all SuperAdmin access requires separately authorized operator recovery; there is no public recovery backdoor.

## Disposable preview verification

Normal preview startup no longer seeds a known administrator/password. For tests only, use a **new** file beneath the preview output's `App_Data/isolated` directory, Development environment and explicit `--admin-fixtures true`. Existing files are rejected. Six synthetic `.invalid` identities receive random four-hour local session tokens. Tokens are written only to ignored `admin-fixtures.json` beside that disposable DB, never evidence. Synthetic Google markers are not verification evidence. Seeded MOCK orders/subscriptions are visibly test records, not provider verification. Do not use the fixture flag against real accounts or production.

Example local run (from Backend, after a Cent.Preview build):

```powershell
$env:ASPNETCORE_ENVIRONMENT='Development'
dotnet .dotnet/phase8-final-preview/Cent.Preview.dll --urls http://127.0.0.1:5128 --isolated-database D:/Lerona/Backend/.dotnet/phase8-final-preview/App_Data/isolated/NEW-UNUSED-NAME.db --admin-fixtures true --import-curriculum true --prepare-subject-offers true --Cent:ClientBaseUrl http://localhost:5198/cent-s --Cent:PublicApiBaseUrl http://127.0.0.1:5128
```

No schema migration was added or applied; the isolated SQLite database uses EnsureCreated from the existing model. Production SQL Server execution, provider concurrency and actual Google sign-in/merchant operations are not claimed by SQLite browser evidence. Version-management visibility remains available even with the timed exam engine disabled; engine-only mutation endpoints are honestly gated/absent in that preview configuration.

## Fresh verification

Cent-only `Cent.Exam.Tests`: **86 passed, zero failed/skipped**, including eight new real-session/policy admin tests; artifact `.dotnet/phase8-test-results/phase8.trx`. Existing exam API tests now use actual Cent admin policies. This suite covers commerce, immutable curriculum/exam behavior and gateway regressions as well as administration. The additional Cent.Tests regression uses the existing shared host in TestServer with an in-memory Cent SQLite database; building its dependencies does not change shared source. No Lerona test suite was executed. Historical 127 Cent/396 Lerona numbers are not represented as fresh results.

Frontend integration, evidence, final changed-file inventory and remaining limitations are recorded in [Phase 8 frontend report](../../../itatest/docs/refinement/PHASE_8_REPORT.md).

The existing `Cent.Tests/CentApiTests.cs` upload regression now registers an isolated account, supplies synthetic verified identity evidence in its in-memory database, binds the operator bootstrap target to that account, and retains the existing session after provisioning. It verifies the SuperAdmin response contains no new token, then exercises the original complete-page upload/publication and immutable-page assertions. The corrected case passed in a disposable test output folder. This is not provisioning of a real account. See the frontend report's sanitized backend verification counters and the broader regression record for exact run boundaries.

The broader 127-case Cent.Tests run also found an outdated assertion in `CentCommerceTests`: duplicate provider-reference reuse is already rejected by the existing payment service with `409/payment_reference_reused`, before a database uniqueness error. The test now requires that exact domain failure while preserving its single-grant and still-pending-order assertions. `CentPayments.cs` was not modified. Exact initial-run and targeted-rerun results are recorded in the frontend report; no full all-green rerun is implied by targeted checks.
