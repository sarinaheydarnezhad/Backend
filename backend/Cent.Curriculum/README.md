# Curriculum sources and checksums

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
