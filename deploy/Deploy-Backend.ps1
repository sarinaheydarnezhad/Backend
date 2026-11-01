[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$stageScript = Join-Path $PSScriptRoot 'Stage-CloudBackupRelease.ps1'
& $stageScript | Tee-Object -Variable stageOutput
$stageLine = $stageOutput | Where-Object { $_ -is [string] -and $_.StartsWith('Staged release: ') } | Select-Object -Last 1
if (-not $stageLine) { throw 'Source staging did not complete. Nothing was deployed.' }
$sourceStage = $stageLine.Substring('Staged release: '.Length)
$descriptor = Get-Content -LiteralPath (Join-Path $sourceStage 'liara.json') -Raw | ConvertFrom-Json
if (-not ($descriptor.disks | Where-Object { $_.name -eq 'backup-keys' -and $_.mountTo -eq '/data/backup-keys' })) {
    throw 'The required durable backup-key mount is missing. Nothing was deployed.'
}

# Keep this explicit flag even though liara.json also declares the disk.
# Disk bindings are release-specific; omitting them on a later release detaches the disk.
& liara.cmd deploy -a lerona-api --path $sourceStage --disks backup-keys:/data/backup-keys --no-app-logs --message 'Backend release with durable backup key storage'
if ($LASTEXITCODE -ne 0) { throw 'Backend deployment failed. Inspect the release status before retrying.' }

$mountOutput = & liara.cmd shell -a lerona-api -c 'mountpoint /data/backup-keys'
$mountLines = @($mountOutput | ForEach-Object { $_.Trim() })
if ($LASTEXITCODE -ne 0 -or $mountLines -notcontains '/data/backup-keys is a mountpoint') {
    throw 'Deployment finished, but the durable backup-key disk mount was not verified. Inspect the live release before accepting it.'
}
Write-Output 'The live backup-key disk mount was verified.'
