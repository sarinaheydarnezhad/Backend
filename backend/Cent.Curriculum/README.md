# Curriculum sources and checksums

## Current additive release — 2026-11-22

The latest candidate is `curriculum-2026-r2.json`, layered over the unchanged r1
candidate and original bundle. Regenerate it with
`node backend/Cent.Curriculum/Authoring/build-release.mjs --expand`.
It adds 19 Persian supplements, 52 English questions and two original passages.
Merged totals: 51 source documents, 247 topics, 487 source sections, 832 exercises,
985 bank questions; 166 original five-option candidates and five shared passages.
The 57 supplements are foundation teaching, not a claim of exhaustive course depth.
The r2 review directory records 430 legacy section audits, 1,485 item audits,
188 independently recomputed numerical keys (zero mismatches), 107 related template
families and four original replacement links. Remaining prose, distractor, optical,
language and rights checks are explicitly unresolved. No source licence is invented.

Import prepares a separate, **disabled DRAFT** exam identity bound to r2 and the
`expired-feedback-v1` policy. It does not change seeded or published blueprints.
Publication and activation are distinct administrator operations; content sign-off
remains absent. Existing simulator entitlement checks guard new starts; terminal
owner review does not require a continuing subscription. No commerce feature is added.
No migration, import, publication or activation was run against an existing database.

The paragraphs below describe the preserved r1 layer.

The original `curriculum.json` and its 39 HTML sources remain unchanged. The
additional `curriculum-2026.json` and six `Sources/*/updates-2026-r1.html` files
provide 38 Persian foundation lessons, 114 original English exercises/questions,
and three original English shared passages. All six subjects are represented.
The merged package has 45 sources, 228 topics, 468 source sections, 780 extracted
or authored exercises and 933 bank questions. Source sections are not the historical
export's database "lessons" count.

Generate the additive artifacts offline, preserving legacy IDs and bytes:

```powershell
node backend/Cent.Curriculum/Authoring/build-release.mjs
powershell -NoProfile -File backend/Cent.Curriculum/Verify-Curriculum.ps1
dotnet test backend/Cent.Exam.Tests/Cent.Exam.Tests.csproj --no-restore
```

The module-only test project references no shared API host or Lerona project.
The `OfflineRelease` verifier is linked into these tests, so validation/export
does not need the legacy author's AngleSharp dependency. Set `CENT_CONTENT_EXPORT_DIR`
to an intended review directory to export the engine manifest while running that
test. Without it, export uses an isolated temporary directory. An optional
`Cent.Curriculum --verify-release <output-directory>` command exposes the same
verifier when the original authoring dependencies are available.

`Content/Materials/cent-content-2026-r1/` contains six standalone printable HTML
booklets. They retain the same syllabus identity and show English questions with
collapsible English solutions; no original PDF/image material is replaced. These
are local review/import artifacts, not publicly served website assets.

`Content/Review/cent-content-2026-r1/` holds the exact source counts, per-item audit
of all 1,485 legacy exercises/questions and the candidate engine manifest.
Agent academic/editorial review is explicitly identified; independent specialist,
language-level/difficulty calibration, rights and optical acceptance of legacy
content remain pending. The original 819 four-option questions are retained for
legacy behavior and are not certified for new Mock Exams.

Migration `20261122081723_VersionedCurriculum` and
`Cent.Module/Deployment/cent-versioned-curriculum.sql` are **review-only, unapplied**.
They add two Cent companion tables after the exam infrastructure migration. An
authorized isolated schema rehearsal and import must precede deployment of the
new content-aware application. Do not use the historical preview import command
below against an existing database for this release; its old SQLite upgrade path
has not been extended/rehearsed here. No runtime flag, publication or database is
changed by generation/verification.

The importer rejects same-ID content conflicts rather than overwriting/retiring
existing content. Reading is not automatically included in subscription access;
sale/entitlement rules remain a separate workstream. New `passage-v1` Practice
requests snapshot context once; old flat Practice and diagnostic clients retain
their old bank. New mock release records retain null administrator review fields
and cannot activate. Syllabus-scoped mastery is explicit; the legacy denominator
remains unchanged.

## Historical original-package workflow

The instructions below describe the original five-subject package. Its counts
and preview upgrades are not instructions to apply the additive release.

Run these commands from the backend repository root. The complete import package is
`backend/Cent.Module/Content/curriculum.json` plus all 39 HTML files under
`backend/Cent.Module/Content/Sources/`. Both are included in Git and embedded in
`Cent.Module` when it builds. No original archive or generator is needed to import.

## Verify a checkout or a received source package

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File backend/Cent.Curriculum/Verify-Curriculum.ps1
```

The script reads JSON as UTF-8, preserves the Persian filename, checks every raw
SHA256, and fails if any file is missing or different. It does not modify files or
the database. An external content folder can be checked with `-ContentDirectory`.

Git's `core.autocrlf=true` can change HTML line endings on Windows even when Git
reports no local edits. The root `.gitattributes` disables text conversion for
these byte-checked HTML files and fixes the JSON package to LF. Keep that file with
the package. Do not work around corruption by disabling checks or replacing hashes
with hashes of unknown files.

If an older checkout already has converted files, first save any intentional local
content edits, then extract the matching repair ZIP over the backend repository
root and rerun verification. Alternatively, use a fresh clone after the updated
`.gitattributes` is committed. Git may keep previously cached working files when
only attributes change, so a plain `git restore` is not a reliable repair for an
existing checkout. A fresh clone or the ZIP replaces the actual bytes.

Keep Unicode filenames intact; do not rename the Persian HTML file. Its path is
also part of the stable source, topic and document identities.

Stop the running preview before rebuilding, then import once:

```powershell
dotnet run --project backend/Cent.Preview/Cent.Preview.csproj -- --upgrade-curriculum true --import-curriculum true
```

This explicit development upgrade backs up the existing SQLite database. A complete
import contains 5 courses, 39 documents and 819 questions. Check
`http://127.0.0.1:5108/api/cent/v1/curriculum`; expect 5 courses and 39 documents.
Use the normal run command without the upgrade/import flags on later starts.
Prices and sale products are configured separately in the administration UI.

## Regenerate deliberately from the supplied originals

`Extract-Sources.ps1` expects `biology.zip`, `math.zip`,
`CEnT-Logic-Booklet-1.zip`, `Physics.rar`, and
`cent-chemistry-ch1-5-deep-1.html` in the input folder. It requires `tar` with RAR
support for the physics archive. This rewrites the source files; it is an authoring
operation, not a prerequisite for an ordinary import.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File backend/Cent.Curriculum/Extract-Sources.ps1 -InputDirectory C:\path\to\originals
dotnet run --project backend/Cent.Curriculum/Cent.Curriculum.csproj
powershell -NoProfile -ExecutionPolicy Bypass -File backend/Cent.Curriculum/Verify-Curriculum.ps1
```

The authoring project parses the source files, computes their raw-byte hashes, and
generates `curriculum.json` and `Content/Review`. Review the generated changes and
run `dotnet test backend/Cent.Tests/Cent.Tests.csproj` before distributing an
updated package. Commit the manifest, sources and `.gitattributes` together.
