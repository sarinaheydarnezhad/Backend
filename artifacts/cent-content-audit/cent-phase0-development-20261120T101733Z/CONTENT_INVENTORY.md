# CEnT-S educational content inventory — DEVELOPMENT DATA

Generated: 2026-11-20T10:17:37.980Z. This is a development preview export for independent review, not a production inventory or academic rewrite.

## Database and sources inspected

The only database opened was `D:\Lerona\Backend\backend\Cent.Preview\App_Data\preview.db` (SQLite), explicitly confirmed by the requester. The export used Node's built-in SQLite provider with `readOnly: true`, extension loading disabled, `PRAGMA query_only=ON`, and one read transaction across all content tables. The application was not started. No SQL Server, remote service, production database, or Lerona fallback connection was used. Database and sidecar bytes/mtime unchanged across extraction: **false**. See [read-only evidence](audit/read_only_evidence.json).

`CentDbContext` owns the `cent_*` entities. `Cent.Preview` replaces its SQL Server provider with SQLite and computes this local path; its startup can create/upgrade/seed data and was deliberately not executed. In the shared host, `ConnectionStrings:CentDb` falls back to `ConnectionStrings:FlashcardsDb`; the design-time factory can also fall back to LocalDB CentDev. Those paths were identified by reading code only. Lerona uses the separate `FlashcardsDbContext`. No Lerona records were read or exported.

The repository bundle is `backend/Cent.Module/Content/curriculum.json`, SHA-256 `5f5c16c6ab02e019de2604160307ed1e1f9adc734fc94ec840947f36f3f287a9`, plus 39 original HTML files under `Content/Sources` and 40 existing derived review files under `Content/Review`. JSON and HTML are embedded resources at build time. The package and existing review files are included byte-for-byte. Compiled assemblies and any deployed content were not inspected.

## Totals and definitions

There are **5 subjects, 43 chapters, 190 topics, 48 lessons/documents, 819 scored-bank questions and 0 reusable stored mock-exam definitions**. The lessons comprise 39 source-based documents and 9 image-based documents; source files, source sections and documents are different representations and must not be summed as separate lessons. There are 666 source exercises, including open-response and quarantined material; these overlap with scored questions and are not an additional disjoint bank.

| Content table | Exported records |
| --- | ---: |
| cent_courses | 5 |
| cent_chapters | 43 |
| cent_topics | 190 |
| cent_trainings | 14 |
| cent_documents | 48 |
| cent_document_pages | 18 |
| cent_document_sections | 4 |
| cent_sources | 39 |
| cent_source_sections | 430 |
| cent_source_exercises | 666 |
| cent_questions | 819 |
| cent_curriculum_imports | 2 |

All rows from these educational tables were exported, including publication/activation flags. No row limit or published-only filter was applied.

## Counts by subject

| Subject | Chapters | Topics | Lessons | Source sections | Source exercises | Questions |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| biology | 5 | 41 | 5 | 78 | 335 | 350 |
| chemistry | 5 | 5 | 1 | 6 | 16 | 31 |
| logic | 20 | 20 | 20 | 174 | 57 | 60 |
| math | 7 | 56 | 7 | 80 | 177 | 174 |
| physics | 6 | 68 | 6 | 92 | 81 | 204 |
| UNASSIGNED | 0 | 0 | 9 | 0 | 0 | 0 |

Unassigned content has no stored subject relationship and was not assigned from title or appearance. Topic counts, exact IDs, question difficulty counts and lesson links are in [counts by topic](derived/counts_by_topic.json). 86 source sections and 0 source exercises lack a TopicId; introductions, navigation and review sections may intentionally be unassigned. 0 topics have no active scored questions. Full lists are in [unmapped content](derived/unmapped_content.json).

## Database versus bundled curriculum

Verdict: **The preview contains the complete bundled curriculum, with matching importer-managed values and matching original HTML; it is not merely a subset.** This does not establish coverage of the official syllabus or production content.

| Content | Bundle | Database | Matching | Differing | Missing from DB | DB-only |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| subjects | 5 | 5 | 5 | 0 | 0 | 0 |
| sources | 39 | 39 | 39 | 0 | 0 | 0 |
| chapters | 43 | 43 | 43 | 0 | 0 | 0 |
| topics | 190 | 190 | 190 | 0 | 0 | 0 |
| source_sections | 430 | 430 | 430 | 0 | 0 | 0 |
| source_exercises | 666 | 666 | 666 | 0 | 0 | 0 |
| questions | 819 | 819 | 819 | 0 | 0 | 0 |
| source_documents | 39 | 48 | 39 | 0 | 0 | 9 |
| curriculum_trainings | 5 | 14 | 5 | 0 | 0 | 9 |

Raw bundled HTML files all match expected checksums and database OriginalHtml: **true**. Current bundle SHA is recorded in curriculum import history: **true**. Import history has 2 records; history alone was not used as proof of completeness. Field-level comparisons independently reproduced the importer mappings and deterministic IDs. JSON option escaping/serialization-only differences: 272; original strings are preserved.

The database-only trainings/documents are included as additional development content. Their repeated titles are duplicate candidates, not evidence that they should be deleted.

## Educational text, choices, versions and assets

Full OriginalHtml/PlainText, source-section Text/Html, exercise Text/Solution, question Text/OptionsJson/CorrectAnswer/Explanation/FigureSvg, relationship IDs, difficulty, origin and flags are preserved. The choice index is zero-based. Ordered parsed choices are also supplied in [answer choices](derived/answer_choices.json). Correct answers have not been re-evaluated academically.

Study-note prose is part of sources/sections. There is no separate editorial study-note table in this model. `cent_notes` contains personal user annotations and is excluded entirely. The 9 image-only lessons retain all 18 stored page BLOBs as checksummed assets; they have no stored machine-readable lesson prose. Original uploaded PDFs are not retained by the application and cannot be exported. No OCR was performed.

Mock exams and diagnostics are generated at runtime with up to 20 questions using subject/topic sampling. No reusable exam-template or question-group table was found. [Mock exam metadata](derived/mock_exams.json) documents source-inspected rules; [question groupings](derived/question_groupings.json) are explicitly derived topic views. Private learner attempts and their snapshots were neither read nor counted.

Publication/activation flags and educational content creation/import times are retained. Package hashes, source checksums and import keys provide provenance. This is a current-state export; no comprehensive editorial revision history or historical question versions are available outside the excluded user-attempt snapshots. Exercise-level Difficulty and FigureSvg exist in the bundle but not in the source-exercise database entity; the bundle preserves them.

All original HTML, inline SVG and WebP BLOBs are preserved. [Asset references](audit/asset_references.json) records HTML/SVG attributes and CSS URLs. External resources were not fetched; font/CDN/site dependencies may need network access when opened. Markup was not executed. This scan is static and does not resolve URLs constructed by JavaScript or every possible CSS escape.

## Completeness and privacy

The educational database export is complete for the 12 explicitly allowlisted tables and fields listed in [schema](audit/schema.json). Missing or differing bundle content, empty fields, broken links and duplicate candidates are detailed in [issues](CONTENT_ISSUES.md). All source rows were exported; no corrections or deduplication were applied. Assets are retained as original bytes.

All user, authentication, payment, purchase, entitlement, personal-note, progress, learner attempt/result/analysis and actor audit tables were excluded without querying their rows. No whole database, backup, credentials, configuration file contents or Lerona data are packaged. See [privacy scope](audit/privacy_scope.json).

## Package navigation and validation

- [CONTENT_SCHEMA.md](CONTENT_SCHEMA.md): entity meanings, keys, relationships and value encoding.
- [CONTENT_ISSUES.md](CONTENT_ISSUES.md): limitations and mechanical findings, with exact record lists in JSON.
- `database/`: one JSON file per educational content type.
- `bundled/`: original curriculum JSON, HTML and existing review files.
- `assets/`: database source HTML and original document-page BLOBs.
- `derived/`: hierarchy, counts, choices, coverage and source-inspected exam metadata.
- `audit/`: comparison, issues, schema, privacy and read-only evidence.
- [EXPORT_MANIFEST.json](EXPORT_MANIFEST.json): file descriptions, counts, byte sizes and SHA-256 values.

The ZIP contains this directory. The manifest hashes every payload file except itself, to avoid a circular digest; the ZIP has a separate SHA-256 sidecar. Archive entries are verified against the manifest after packaging.
