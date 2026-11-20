# CEnT-S content schema — DEVELOPMENT DATA

This describes the actual confirmed SQLite preview and source models. Actual content-only table metadata, defaults and index definitions are in [audit/schema.json](audit/schema.json).

## Entity meanings and relationships

| Audit concept | Stored representation | Relationship |
| --- | --- | --- |
| Subject | CentCourse / cent_courses | Code is stable key; optional TrainingId |
| Chapter | CentChapter / cent_chapters | CourseCode + SourceId |
| Topic | CentTopic / cent_topics | ChapterId + CourseCode |
| Sale/access grouping | CentTraining / cent_trainings | Documents belong to TrainingId; only educational metadata exported |
| Lesson | CentDocument / cent_documents | TrainingId and optional SourceId |
| Source lesson text | CentSource / cent_sources | CourseCode; full OriginalHtml and PlainText |
| Lesson section / virtual page | CentSourceSection / cent_source_sections | SourceId; optional TopicId; ordered Text/Html |
| Image lesson page | CentDocumentPage / cent_document_pages | Composite DocumentId + Number; Image BLOB |
| Image lesson outline | CentSection / cent_document_sections | DocumentId + StartPage |
| Original exercise | CentSourceExercise / cent_source_exercises | SourceSectionId, optional TopicId; original answer and Solution |
| Scored question | CentQuestion / cent_questions | Subject; optional TopicId, SourceSectionId, SourceExerciseId |
| Answer choices | OptionsJson on questions/exercises | Ordered array of strings; no stored choice IDs |
| Correct answer | CorrectAnswer / OriginalCorrectAnswer | Zero-based option index; original exercise answer can be null |
| Explanation | Explanation / Solution | Full stored prose |
| Editorial study notes | No separate entity | Teaching prose is in sources and source sections |
| Personal notes | CentNote / cent_notes | User-owned; excluded without reading rows |
| Question groups | No separate stored entity | Runtime grouping by Subject and TopicId/Topic; derived topic groups supplied |
| Reusable mock exams | No separate stored entity | Runtime question sampling; no reusable exam definition records |
| Learner mock attempts | CentAttempt + CentAttemptQuestion | User-owned; excluded, including historic question snapshots |
| Import provenance | CentCurriculumImport | PackageHash and aggregate counts/import time; no actor data |

Source exercises and scored questions are related but have different purposes. Open-response and quarantined source exercises can exist without scored questions. Source sections can intentionally have no topic. An image-only lesson does not have a SourceId and uses page images and document sections instead of source-section virtual pages. No official syllabus alignment entity or external question bank integration was identified in these models.

## Export encoding

All generated text files use UTF-8 without a BOM. Original bundled files are copied without byte transformation. Each database JSON uses the envelope `{ datasetLabel, source, recordCount, records }`. Column names, stored GUID strings, import keys, option JSON strings, nulls and flags are preserved. GUID comparisons are case-insensitive; no exported IDs were changed. Deterministic bundled GUID derivation is documented in [comparison](audit/comparison.json).

SQLite integer flags remain 0/1. Integers outside JavaScript's safe range are decimal strings, preserving every digit. In particular, preview DateTimeOffset values are .NET UTC ticks (100 ns since 0001-01-01), exported as strings; they are not Unix milliseconds. To derive Unix milliseconds use `(BigInt(ticks) - 621355968000000000n) / 10000n`. No timestamp rounding was applied to the source fields.

The page Image field becomes `{ encoding: "external-file", path, bytes, sha256, mediaType }`; the exact BLOB is stored at that path, relative to the package root. All other database fields remain direct stored values. Database source OriginalHtml is additionally written to a per-source UTF-8 HTML file.

Derived answer choices use the natural composite identity `parentTable + parentId + position`; the database has no independent choice IDs. The derived boolean `correct` follows the stored index and can be null for original exercises with no answer index. It does not validate the answer academically.

## Versions and publication

Trainings/documents retain Published, questions/courses retain Active, exercises retain Status/ReviewNote, questions retain Origin/ImportKey/FigureSvg, and sources retain Sha256/ImportedAt. CurriculumImports retains package hashes and import counts. A package import receipt does not contain the old package itself. No separate full editorial revision history or question version column exists. Personal-note Revision is not an editorial content version and was excluded with its user-owned table.

## Bundled content outside the database

`Content/curriculum.json` contains sources, chapters, topics, sections, exercises, questions and existing review findings. The HTML files under Sources contain original formatting, inline diagrams, teaching text and references. SourceReader creates simplified/sanitized section HTML and plain text; those derivatives can omit visual elements which remain in original HTML. Source-exercise DTO Difficulty and FigureSvg have no corresponding database columns and remain available in the bundled JSON. Review text/inventory is a derived authoring output, not another independent set of lessons.

`Cent.Curriculum/SourceReader.cs`, QuestionAuthoring.cs, OtherAuthoring.cs and PhysicsAuthoring.cs implement extraction/authored question generation; they were not run. `Cent.Module.csproj` embeds the source package. Original upload PDFs and original author-supplied archives are not persisted by these content models. No external asset download was attempted.

## Actual exported tables

### cent_courses → database/subjects.json

Primary key: Code. Rows: 5.

| Column | SQLite type | Nullable | Primary key order |
| --- | --- | --- | ---: |
| Code | TEXT | no | 1 |
| Title | TEXT | no | 0 |
| EnglishTitle | TEXT | no | 0 |
| SortOrder | INTEGER | no | 0 |
| TrainingId | TEXT | yes | 0 |
| Active | INTEGER | no | 0 |

Foreign keys: TrainingId → cent_trainings.Id.

### cent_chapters → database/chapters.json

Primary key: Id. Rows: 43.

| Column | SQLite type | Nullable | Primary key order |
| --- | --- | --- | ---: |
| Id | TEXT | no | 1 |
| ImportKey | TEXT | no | 0 |
| CourseCode | TEXT | no | 0 |
| SourceId | TEXT | no | 0 |
| Title | TEXT | no | 0 |
| SortOrder | INTEGER | no | 0 |

Foreign keys: SourceId → cent_sources.Id; CourseCode → cent_courses.Code.

### cent_topics → database/topics.json

Primary key: Id. Rows: 190.

| Column | SQLite type | Nullable | Primary key order |
| --- | --- | --- | ---: |
| Id | TEXT | no | 1 |
| ImportKey | TEXT | no | 0 |
| ChapterId | TEXT | no | 0 |
| CourseCode | TEXT | no | 0 |
| Title | TEXT | no | 0 |
| EnglishTitle | TEXT | no | 0 |
| SortOrder | INTEGER | no | 0 |
| Foundation | INTEGER | no | 0 |

Foreign keys: CourseCode → cent_courses.Code; ChapterId → cent_chapters.Id.

### cent_trainings → database/trainings.json

Primary key: Id. Rows: 14.

| Column | SQLite type | Nullable | Primary key order |
| --- | --- | --- | ---: |
| Id | TEXT | no | 1 |
| Title | TEXT | no | 0 |
| Description | TEXT | no | 0 |
| IncludedInSubscription | INTEGER | no | 0 |
| Published | INTEGER | no | 0 |
| CreatedAt | INTEGER | no | 0 |

Foreign keys: none declared.

### cent_documents → database/lessons.json

Primary key: Id. Rows: 48.

| Column | SQLite type | Nullable | Primary key order |
| --- | --- | --- | ---: |
| Id | TEXT | no | 1 |
| TrainingId | TEXT | no | 0 |
| SourceId | TEXT | yes | 0 |
| Title | TEXT | no | 0 |
| SortOrder | INTEGER | no | 0 |
| PageCount | INTEGER | no | 0 |
| Published | INTEGER | no | 0 |
| CreatedAt | INTEGER | no | 0 |

Foreign keys: TrainingId → cent_trainings.Id; SourceId → cent_sources.Id.

### cent_document_pages → database/document_pages.json

Primary key: DocumentId + Number. Rows: 18.

| Column | SQLite type | Nullable | Primary key order |
| --- | --- | --- | ---: |
| DocumentId | TEXT | no | 1 |
| Number | INTEGER | no | 2 |
| Image | BLOB | no | 0 |
| Width | INTEGER | no | 0 |
| Height | INTEGER | no | 0 |

Foreign keys: DocumentId → cent_documents.Id.

### cent_document_sections → database/document_sections.json

Primary key: Id. Rows: 4.

| Column | SQLite type | Nullable | Primary key order |
| --- | --- | --- | ---: |
| Id | TEXT | no | 1 |
| DocumentId | TEXT | no | 0 |
| Title | TEXT | no | 0 |
| StartPage | INTEGER | no | 0 |

Foreign keys: DocumentId → cent_documents.Id.

### cent_sources → database/sources.json

Primary key: Id. Rows: 39.

| Column | SQLite type | Nullable | Primary key order |
| --- | --- | --- | ---: |
| Id | TEXT | no | 1 |
| ImportKey | TEXT | no | 0 |
| CourseCode | TEXT | no | 0 |
| FileName | TEXT | no | 0 |
| Title | TEXT | no | 0 |
| OriginalTitle | TEXT | no | 0 |
| Sha256 | TEXT | no | 0 |
| OriginalHtml | TEXT | no | 0 |
| PlainText | TEXT | no | 0 |
| ImportedAt | INTEGER | no | 0 |

Foreign keys: CourseCode → cent_courses.Code.

### cent_source_sections → database/source_sections.json

Primary key: Id. Rows: 430.

| Column | SQLite type | Nullable | Primary key order |
| --- | --- | --- | ---: |
| Id | TEXT | no | 1 |
| SourceId | TEXT | no | 0 |
| TopicId | TEXT | yes | 0 |
| ImportKey | TEXT | no | 0 |
| Heading | TEXT | no | 0 |
| SortOrder | INTEGER | no | 0 |
| Text | TEXT | no | 0 |
| Html | TEXT | no | 0 |

Foreign keys: TopicId → cent_topics.Id; SourceId → cent_sources.Id.

### cent_source_exercises → database/source_exercises.json

Primary key: Id. Rows: 666.

| Column | SQLite type | Nullable | Primary key order |
| --- | --- | --- | ---: |
| Id | TEXT | no | 1 |
| SourceSectionId | TEXT | no | 0 |
| TopicId | TEXT | yes | 0 |
| ImportKey | TEXT | no | 0 |
| Text | TEXT | no | 0 |
| OptionsJson | TEXT | no | 0 |
| OriginalCorrectAnswer | INTEGER | yes | 0 |
| Solution | TEXT | no | 0 |
| Status | TEXT | no | 0 |
| ReviewNote | TEXT | yes | 0 |

Foreign keys: TopicId → cent_topics.Id; SourceSectionId → cent_source_sections.Id.

### cent_questions → database/questions.json

Primary key: Id. Rows: 819.

| Column | SQLite type | Nullable | Primary key order |
| --- | --- | --- | ---: |
| Id | TEXT | no | 1 |
| Subject | TEXT | no | 0 |
| TopicId | TEXT | yes | 0 |
| SourceSectionId | TEXT | yes | 0 |
| SourceExerciseId | TEXT | yes | 0 |
| ImportKey | TEXT | yes | 0 |
| Origin | TEXT | no | 0 |
| FigureSvg | TEXT | yes | 0 |
| Topic | TEXT | no | 0 |
| Difficulty | TEXT | no | 0 |
| Text | TEXT | no | 0 |
| OptionsJson | TEXT | no | 0 |
| CorrectAnswer | INTEGER | no | 0 |
| Explanation | TEXT | no | 0 |
| Kind | TEXT | no | 0 |
| Active | INTEGER | no | 0 |

Foreign keys: TopicId → cent_topics.Id; SourceSectionId → cent_source_sections.Id; SourceExerciseId → cent_source_exercises.Id.

### cent_curriculum_imports → database/curriculum_imports.json

Primary key: PackageHash. Rows: 2.

| Column | SQLite type | Nullable | Primary key order |
| --- | --- | --- | ---: |
| PackageHash | TEXT | no | 1 |
| ImportedAt | INTEGER | no | 0 |
| SourceCount | INTEGER | no | 0 |
| TopicCount | INTEGER | no | 0 |
| QuestionCount | INTEGER | no | 0 |

Foreign keys: none declared.

