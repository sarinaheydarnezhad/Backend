param([Parameter(Mandatory=$true)][string]$PayloadDirectory)
$ErrorActionPreference = 'Stop'
$auditPayload = (Resolve-Path -LiteralPath $PayloadDirectory).Path
$auditAllowedRoot = (Resolve-Path -LiteralPath 'D:/Lerona/Backend/artifacts/cent-content-audit').Path
if (-not $auditPayload.StartsWith($auditAllowedRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Payload must be inside the audit output directory.' }
$auditZip = $auditPayload + '.zip'
if (Test-Path -LiteralPath $auditZip) { throw 'Refusing to overwrite an existing archive.' }
$auditManifest = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $auditPayload 'EXPORT_MANIFEST.json') | ConvertFrom-Json
if ($auditManifest.datasetLabel -ne 'DEVELOPMENT DATA' -or $auditManifest.verification.result -ne 'PASS') { throw 'A verified development payload is required.' }
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($auditPayload, $auditZip, [IO.Compression.CompressionLevel]::Optimal, $false, [Text.Encoding]::UTF8)
$auditExpected = @{}
foreach ($auditFile in $auditManifest.files) { $auditExpected[$auditFile.path] = $auditFile }
$auditManifestFile = Get-Item -LiteralPath (Join-Path $auditPayload 'EXPORT_MANIFEST.json')
$auditExpected['EXPORT_MANIFEST.json'] = [pscustomobject]@{ bytes = $auditManifestFile.Length; sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $auditManifestFile.FullName).Hash.ToLowerInvariant() }
$auditArchive = [IO.Compression.ZipFile]::OpenRead($auditZip)
$auditSeen = @{}
try {
    foreach ($auditEntry in $auditArchive.Entries) {
        if ($auditEntry.FullName.EndsWith('/')) { continue }
        $auditEntryName = $auditEntry.FullName.Replace('\','/')
        if ($auditSeen.ContainsKey($auditEntryName)) { throw ('Duplicate archive entry: ' + $auditEntryName) }
        if (-not $auditExpected.ContainsKey($auditEntryName)) { throw ('Unexpected archive entry: ' + $auditEntryName) }
        if ($auditEntryName -match '(?i)(^|/)(\.env[^/]*|preview\.db[^/]*|appsettings[^/]*|[^/]*\.(bak|mdf))$') { throw ('Prohibited archive entry: ' + $auditEntryName) }
        $auditExpectedFile = $auditExpected[$auditEntryName]
        if ($auditEntry.Length -ne $auditExpectedFile.bytes) { throw ('Archive size mismatch: ' + $auditEntryName) }
        $auditStream = $auditEntry.Open()
        $auditHasher = [Security.Cryptography.SHA256]::Create()
        try { $auditDigest = ([BitConverter]::ToString($auditHasher.ComputeHash($auditStream))).Replace('-','').ToLowerInvariant() }
        finally { $auditHasher.Dispose(); $auditStream.Dispose() }
        if ($auditDigest -ne $auditExpectedFile.sha256) { throw ('Archive digest mismatch: ' + $auditEntryName) }
        $auditSeen[$auditEntryName] = $true
    }
} finally { $auditArchive.Dispose() }
if ($auditSeen.Count -ne $auditExpected.Count) { throw 'Archive entry count mismatch.' }
$auditZipHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $auditZip).Hash.ToLowerInvariant()
$auditEncoding = New-Object Text.UTF8Encoding($false)
[IO.File]::WriteAllText($auditZip + '.sha256', $auditZipHash + '  ' + [IO.Path]::GetFileName($auditZip) + [Environment]::NewLine, $auditEncoding)
$auditReceipt = [ordered]@{ datasetLabel = 'DEVELOPMENT DATA'; result = 'PASS'; verifiedUtc = [DateTimeOffset]::UtcNow.ToString('o'); archive = $auditZip; archiveBytes = (Get-Item -LiteralPath $auditZip).Length; sha256 = $auditZipHash; verifiedEntries = $auditSeen.Count; allPathsSizesAndHashesMatch = $true; prohibitedDatabaseOrConfigurationFiles = 0 }
[IO.File]::WriteAllText($auditZip + '.verification.json', ($auditReceipt | ConvertTo-Json), $auditEncoding)
$auditReceipt | ConvertTo-Json
