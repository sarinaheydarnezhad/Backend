param([Parameter(Mandatory=$true)][string]$InputDirectory)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$contentRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../Cent.Module/Content/Sources'))
New-Item -ItemType Directory -Path $contentRoot -Force | Out-Null
$archives = @{ 'biology.zip'='biology'; 'math.zip'='math'; 'CEnT-Logic-Booklet-1.zip'='logic' }
foreach ($archiveName in $archives.Keys) {
    $destination = Join-Path $contentRoot $archives[$archiveName]
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    $archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $InputDirectory $archiveName))
    try {
        foreach ($entry in $archive.Entries) {
            if (-not $entry.FullName.EndsWith('.html', [StringComparison]::OrdinalIgnoreCase)) { continue }
            $target = [IO.Path]::GetFullPath((Join-Path $destination $entry.FullName))
            if (-not $target.StartsWith($destination + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'An archive entry escapes its source directory.' }
            New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
        }
    } finally { $archive.Dispose() }
}
$physicsPath = Join-Path $InputDirectory 'Physics.rar'
$physicsDestination = Join-Path $contentRoot 'physics'
$entries = @(& tar -tf $physicsPath)
if ($LASTEXITCODE -ne 0) { throw 'Could not inspect the physics archive.' }
foreach ($entry in $entries) {
    if ([IO.Path]::IsPathRooted($entry) -or $entry -match '(^|[/\\])\.\.([/\\]|$)' -or -not $entry.EndsWith('.html')) { throw 'Unexpected physics archive entry.' }
}
New-Item -ItemType Directory -Path $physicsDestination -Force | Out-Null
& tar -xf $physicsPath -C $physicsDestination
if ($LASTEXITCODE -ne 0) { throw 'Could not extract the physics archive.' }
$chemistryDestination = Join-Path $contentRoot 'chemistry'
New-Item -ItemType Directory -Path $chemistryDestination -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $InputDirectory 'cent-chemistry-ch1-5-deep-1.html') -Destination $chemistryDestination -Force
Write-Output "Extracted source HTML files: $((Get-ChildItem -LiteralPath $contentRoot -File -Recurse).Count)"
