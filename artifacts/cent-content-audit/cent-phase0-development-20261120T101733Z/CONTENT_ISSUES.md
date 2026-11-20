# CEnT-S export issues — DEVELOPMENT DATA

This is a mechanical inventory, not an academic or syllabus judgment. No educational text, keys, statuses or relationships were corrected. Exact affected identities and details are in [audit/issues.json](audit/issues.json).

## Completeness and comparison

Full bundled identities present: **true**. All importer-managed values and raw HTML match: **true**. Missing bundled database identities: **0**. Differing database records: **0**. HTML/checksum mismatches: **0**. Inspect [comparison](audit/comparison.json) for exact fields and IDs, including any database-only records. Question option JSON serialization-only differences are listed separately and are not content differences.

## Relationships and incomplete content

- Broken declared educational foreign keys: **0**. All declared content foreign keys were checked against the exported parent records without reading excluded tables.
- Cross-subject semantic link checks and document page/section bounds are included in the issue list. This is not a whole-database integrity or corruption check.
- Questions whose TopicId differs from their linked original exercise: **8**. Both mappings match the bundle, so this is not database drift or a broken foreign key. It may reflect intentional retagging; exact question/exercise IDs and topic titles are preserved for editorial review.
- Source sections without TopicId: **86**. Source exercises without TopicId: **0**. These nullable mappings can be intentional; exact IDs are supplied.
- Topics without active scored questions: **0**. The complete source corpus can therefore still have assessment coverage gaps.
- Image-only lessons: **9**, with **18** exported images. They have no stored machine-readable full lesson text or original PDF. No OCR was invented.
- Standalone editorial study notes: no separate table was found; full teaching text is preserved in sources/sections. Personal learner notes were intentionally excluded.
- Reusable stored mock exam templates: **0**. Runtime sampling rules are supplied. Private attempts/results, including any historical snapshots, are outside scope and were not read or counted.

### Empty or missing prose/labels

| Field | Affected records |
| --- | ---: |
| cent_topics.EnglishTitle | 78 |

Optional missing English titles and empty section text are reported separately from invalid question fields in the detailed issue records. Source text remains available in its full original HTML even where extraction produced empty derivatives.

## Source exercises and prior authoring findings

Source exercise status counts: `{"open-response":177,"quarantined":1,"source-mcq":488}`. Missing original answer indices may correspond to legitimate open-response exercises. Scored-bank validation checks JSON parsing, at least two choices, non-empty/unique choices, answer bounds, question/explanation text and recognized difficulty; it does not prove correct answers.

The bundle already contains 5 authoring findings, preserved unchanged below. They describe earlier authoring decisions; this audit applied no corrections:

- `logic/ch06-consistency.html#exercise-002` — **quarantined**: The explanation changes an unconditional universal statement into a conditional. The options also overlap; this item is excluded from scoring.
- `logic/ch08-equivalents.html#exercise-003` — **clarified-option**: Corrected the direction named in option A: the conclusion uses B→A, not A→B.
- `logic/ch11-square.html#exercise-002` — **explicit-assumption**: Added a non-empty domain so the traditional square-of-opposition answer is valid.
- `logic/ch14-barcharts.html#exercise-002` — **corrected-key**: Corrected A to C: both absolute increases equal 60 tons.
- `logic/ch13-quantifiers.html` — **canonical-title**: The HTML title says chapter 12. The visible chapter 13 heading is used for the catalog; original HTML is preserved unchanged.

## Duplicate candidates

There are **6 candidate groups**, potentially overlapping. Criteria are whitespace-normalized exact statement + ordered options, repeated lesson/training titles, identical ordered image-page hashes, or identical source HTML. Repeated titles alone do not establish identical lesson text; ordered page hashes establish byte-identical image sets. No fuzzy or academic-equivalence detection was performed and no records were removed.

| Table | Criterion | Records | IDs |
| --- | --- | ---: | --- |
| cent_documents | same normalized title | 6 | 02B7FEB1-CCE1-4EB3-BF77-61F390571AC5, 0FEA7086-ED9E-41A0-9323-E2E42C9B20BD, 705262B2-72ED-4FCD-839A-50D76C6B49DE, 9DF88A6A-17F1-4377-BF4C-8947A3C7ACE7, D3DDCA98-78FD-48F0-A91C-EAA61C44D99C, FC18C2D6-E80F-460C-ACE3-D848FBE50F1E |
| cent_documents | same normalized title | 3 | 29856693-615D-4665-B11B-98CD6E7E57AD, 43A380FF-1A22-401B-8729-8449ADC673CB, 9DCCE30C-576A-46A0-B6C1-4ABF6EC75E7E |
| cent_documents | identical ordered page-image hashes (image-only lessons) | 6 | 02B7FEB1-CCE1-4EB3-BF77-61F390571AC5, 0FEA7086-ED9E-41A0-9323-E2E42C9B20BD, 705262B2-72ED-4FCD-839A-50D76C6B49DE, 9DF88A6A-17F1-4377-BF4C-8947A3C7ACE7, D3DDCA98-78FD-48F0-A91C-EAA61C44D99C, FC18C2D6-E80F-460C-ACE3-D848FBE50F1E |
| cent_documents | identical ordered page-image hashes (image-only lessons) | 3 | 29856693-615D-4665-B11B-98CD6E7E57AD, 43A380FF-1A22-401B-8729-8449ADC673CB, 9DCCE30C-576A-46A0-B6C1-4ABF6EC75E7E |
| cent_trainings | same normalized title | 6 | 0B96F1DE-764A-47F7-931B-2FDDFFB3E681, 8B53B160-10DB-46CC-9A06-F4F128553B4D, CAA10614-F0E6-4F56-84E8-69167147066C, EB0BD398-C645-4DD1-B47E-633904D18C67, EE17FD5D-ADE1-43A6-8E3B-C61ED983E56A, F271F06A-9425-464D-95D6-55B5452316F1 |
| cent_trainings | same normalized title | 3 | 21EF91B0-71F2-4BEC-A0ED-C2F5AD24E0B8, 602AB3A0-D200-4B2D-80E9-DC9157389F92, AED10460-71F5-4878-A3AD-3E570A9F9AD5 |

See [duplicate candidates](audit/duplicate_candidates.json) for machine-readable groups/fingerprints. Extra repeated development lessons may be fixture material; their provenance is not established by this read-only inspection.

## Assets and unavailable sources

Static reference statuses: `{"external-not-fetched":134,"in-document-fragment":532}`. All local page BLOBs, database OriginalHtml, bundled HTML and inline SVGs are preserved. References remain exactly as stored. External resources were not fetched or verified. Missing local files, if any, are listed as UNRESOLVED_ASSET_REFERENCE in issues.json. Static scanning covers common HTML/SVG attributes and CSS URLs; dynamically constructed references and all possible CSS encodings are outside the check.

Original upload PDFs, original input ZIP/RAR archives, unpublished files outside the confirmed repository bundle, production records, deployed assembly resources and excluded learner snapshots were not available within the authorized content scope. The JSON/HTML bundle is self-contained for its text and inline figures, but original HTML can retain network-dependent fonts or styles.

## Provenance and operating limits

- **DEVELOPMENT DATA** does not establish production parity or official CEnT-S syllabus coverage.
- This snapshot includes all allowlisted educational rows. It is not a whole-database backup, and private or commercial records are excluded by design.
- Existing import receipts and content hashes are retained; comprehensive editorial revision history is not stored in these content entities.
- Database/sidecar fingerprint unchanged during extraction: **false**. A single read transaction provided a consistent content snapshot; the audit did not stop any running process or disable external writers.
- Full prose and raw markup were exported without execution. Opening original HTML may request its external dependencies; the audit did not execute it.

## Mechanical finding totals

Severity counts: `{"error":0,"warning":10,"info":270}`. Informational records identify expected nullable fields, open-response items or image-only lessons; they are not necessarily defects.

| Finding code | Records |
| --- | ---: |
| DUPLICATE_CANDIDATE | 6 |
| IMAGE_ONLY_LESSON | 9 |
| MISSING_ENGLISH_TOPIC_TITLE | 78 |
| QUESTION_EXERCISE_TOPIC_MISMATCH | 8 |
| SOURCE_CHANGED_DURING_READ | 1 |
| SOURCE_EXERCISE_REVIEW_STATUS | 1 |
| SOURCE_EXERCISE_WITHOUT_ANSWER_INDEX | 177 |
