# Card content and synchronization transport contract

Logical lengths are UTF-16 code units (`string.length` in JavaScript, `String.Length` in .NET), measured before trimming. No Unicode normalization, transliteration, truncation, or arbitrary conversion is permitted. Text must contain well-formed Unicode; paired supplementary characters consume two units. Trim uses the ECMAScript WhiteSpace/LineTerminator set on both platforms. Interior whitespace, Persian, RTL marks, emoji, line breaks, and combining marks are preserved.

| Field | Type | Required | Maximum Length | Normalization | Validation Behavior |
|---|---|---|---:|---|---|
| frontText | string | Yes | 4,000 | Trim edges | Empty/whitespace rejected |
| meaning (back) | string | Yes | 4,000 | Trim edges | Empty/whitespace rejected |
| phonetic | string or null | No | 500 | Trim; empty becomes null | Missing/null allowed; other types rejected |
| category | string or null | No | 120 | Trim; empty becomes null | Missing/null allowed; other types rejected |
| examples | array | Yes | 20 entries | Preserve order | Empty allowed; missing/null/non-array rejected |
| examples[i] | object | Yes | Three known keys | No coercion | Unknown keys and non-objects rejected |
| examples[i].sentence | string | Yes | 4,000 | Trim edges | Empty/whitespace rejected |
| examples[i].translation | string | No | 4,000 | Trim; empty omitted | Missing allowed; explicit null and other types rejected |
| examples[i].notes | string | No | 4,000 | Trim; empty omitted | Missing allowed; explicit null and other types rejected |

The existing entity identity, deck relationship, timestamps, and archive metadata remain unchanged. No new card-content fields are introduced. Import-only extra columns/tags remain governed by existing transfer behavior. Unknown fields in wire mutation payloads are rejected; remote snapshots may additionally carry existing server metadata.

Maximum logical content: `2*4000 + 500 + 120 + 20*3*4000 = 248,620` UTF-16 units. Canonical JSON needs at most six bytes per unit (including escaped control characters, quotation marks, backslashes, Persian escaped by the default .NET encoder, and surrogate escapes). A 2,048-byte structural allowance covers the fixed property names, brackets, separators, null literals, and deck UUID. Card payload transport limit: **1,493,768 UTF-8 bytes** (`6*248620+2048`). This is a transport safety ceiling, not an additional allowance for unknown fields. All logical maxima can coexist.

Mutation bodies remain JSON objects with at most 50 operations. Normal push is bounded to **2,097,152 UTF-8 bytes** including the envelope. The client sends one operation per push, sequentially; it does not combine 25 pending operations into one request. A single maximum card plus its operation envelope fits. External batch clients must pack by actual serialized byte size as well as operation count; an oversized whole request receives 413 before any write. Guest migration retains its existing 10,485,760-byte and 10,000-operation limits, atomically.

Pull pages remain contiguous and have at most 100 operations. Payload bytes plus a per-operation metadata reserve are budgeted to 2,097,152 bytes; card checkpoints are included in that reserve. A legacy already-stored operation exceeding this budget blocks its page with 409 `sync_payload_recovery_required`; its original content and cursor are retained for controlled recovery. It is never silently skipped or returned as an unbounded response.

Normalization is performed by local content entry and by domain reads. The backend trims front/back with the same ECMAScript set. It preserves accepted optional/nested wire text verbatim for compatibility with existing receipts and snapshots. Canonical validation uses the same pre-trim bounds and required-text rules in both languages; normalization never changes the meaning of interior text. Known format-1 backup arrays of strings have a lossless restore adapter to `{sentence: string}` objects; the original encrypted artifact is retained. This legacy adapter is not accepted by the normal mutation validator.

The maximum compact structural overhead, measured with all 20 three-field objects and a UUID deck ID, is 998 bytes. The 2,048-byte allowance leaves 1,050 bytes for optional snapshot metadata and serialization variations without depending on whitespace chosen by a client. Transport limits count actual UTF-8 bytes. Logical limits do not count Unicode code points or user-perceived graphemes.

Import files retain their existing 50 MiB bound. APKG decompressed collections retain their 100 MiB bound. XLSX example JSON uses existing continuation cells split at 30,000 UTF-16 units without breaking surrogate pairs; this is transport segmentation, not card truncation.

SQLite TEXT and SQL Server `nvarchar(max)` examples/feed payload columns do not impose the old 16,384-character bound. SQL Server front/back remain `nvarchar(4000)`, phonetic `nvarchar(500)`, category `nvarchar(120)`. No schema migration is needed. Backups retain their configured account-wide byte cap (default 10 MiB); maximum individual cards fit, but arbitrarily large accounts do not. Oversized accounts fail explicitly and keep source data intact.

Deploy backend before frontend. Scheduling contract stays version 2. Card content capability is additive; large uploads check it before sending to an older backend. Small compatible cards remain usable with a Phase 1 backend. Validation failures never acknowledge a card or advance a failed pull cursor.
