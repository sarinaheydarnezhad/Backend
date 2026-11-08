param(
    [string]$ContentDirectory = (Join-Path $PSScriptRoot '../Cent.Module/Content')
)

$ErrorActionPreference = 'Stop'
$contentRoot = [IO.Path]::GetFullPath($ContentDirectory)
$packagePath = Join-Path $contentRoot 'curriculum.json'
$sourceRoot = [IO.Path]::GetFullPath((Join-Path $contentRoot 'Sources'))
# Windows PowerShell's default encoding corrupts Persian filenames in JSON.
$utf8 = New-Object System.Text.UTF8Encoding($false, $true)
$package = [IO.File]::ReadAllText($packagePath, $utf8) | ConvertFrom-Json
if (@($package.sources).Count -eq 0) { throw 'The curriculum package has no sources.' }
$duplicates = @($package.sources | Group-Object file | Where-Object Count -gt 1)
if ($duplicates.Count -gt 0) { throw 'The curriculum package contains duplicate source filenames.' }

$missing = 0
$mismatches = 0
foreach ($source in $package.sources) {
    $path = [IO.Path]::GetFullPath((Join-Path $sourceRoot $source.file))
    if (-not $path.StartsWith($sourceRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw ('Source path escapes the Sources directory: ' + $source.file)
    }
    if (-not [IO.File]::Exists($path)) {
        Write-Warning ('Missing source: ' + $source.file)
        $missing++
        continue
    }
    $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $source.sha256) {
        Write-Warning ('Source checksum mismatch: ' + $source.file + ' (expected ' + $source.sha256 + ', actual ' + $actual + ')')
        $mismatches++
    }
}
if ($missing -gt 0 -or $mismatches -gt 0) {
    throw ('Curriculum verification failed: {0} missing source(s), {1} checksum mismatch(es). Restore the matching source package; do not bypass checksum validation.' -f $missing, $mismatches)
}
Write-Output ('Verified all {0} curriculum sources; no missing files or checksum mismatches.' -f @($package.sources).Count)
Write-Output ('Package SHA256: ' + (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant())
