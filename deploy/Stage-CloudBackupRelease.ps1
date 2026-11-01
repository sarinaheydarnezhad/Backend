[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repository = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$stage = Join-Path $repository ('.dotnet/cloud-backup-source-' + [Guid]::NewGuid().ToString('N'))
$verifiedPublish = Join-Path $repository ('.dotnet/cloud-backup-build-' + [Guid]::NewGuid().ToString('N'))
$compileOutput = 'bin/cloud-backup-' + [Guid]::NewGuid().ToString('N') + '/'
$project = Join-Path $repository 'backend/Flashcards.Api/Flashcards.Api.csproj'
$retainedConfiguration = Join-Path $repository 'backend/publish'
$keyRingPath = '/data/backup-keys'

& dotnet publish $project --no-restore --disable-build-servers -c Release -m:1 -p:UseSharedCompilation=false "-p:OutputPath=$compileOutput" -o $verifiedPublish --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw 'Publication failed. No deployment was performed.' }

New-Item -ItemType Directory -Path $stage | Out-Null
foreach ($projectName in @('Flashcards.Api', 'Flashcards.Application', 'Flashcards.Domain', 'Flashcards.Infrastructure')) {
    $projectRoot = Join-Path $repository ('backend/' + $projectName)
    foreach ($file in Get-ChildItem -LiteralPath $projectRoot -Recurse -File) {
        $relative = $file.FullName.Substring($projectRoot.Length).TrimStart([char[]]'\/')
        if ($relative -match '(^|[\\/])(bin|obj)([\\/]|$)' -or
            $file.Name -eq 'appsettings.Local.json' -or $file.Name -like 'appsettings.*.local.json' -or
            $file.Extension -in @('.user', '.pfx')) { continue }
        $target = Join-Path (Join-Path $stage $projectName) $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
}
$apiStage = Join-Path $stage 'Flashcards.Api'
foreach ($file in Get-ChildItem -LiteralPath $retainedConfiguration -File) {
    if ($file.Name -eq 'appsettings.Local.json' -or $file.Name -like 'appsettings.*.local.json') { continue }
    if ($file.Name -like 'appsettings*.json' -or $file.Name -eq 'web.config') {
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $apiStage $file.Name)
    }
}
$productionPath = Join-Path $apiStage 'appsettings.Production.json'
$production = Get-Content -LiteralPath $productionPath -Raw | ConvertFrom-Json
if ($production.Backups.KeyRingPath -and $production.Backups.KeyRingPath -ne $keyRingPath) {
    throw 'An existing backup key-ring path must be recovered and reviewed before changing its mount. Nothing was deployed.'
}
if (-not $production.Backups) {
    $production | Add-Member -NotePropertyName Backups -NotePropertyValue ([pscustomobject]@{})
}
$production.Backups | Add-Member -NotePropertyName KeyRingPath -NotePropertyValue $keyRingPath -Force
$production | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $productionPath -Encoding UTF8
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'liara.backups.json') -Destination (Join-Path $stage 'liara.json')
Write-Output "Staged release: $stage"
Write-Output 'This source package includes the .csproj files required by the native Liara .NET builder.'
Write-Output 'The backup-keys disk must be provisioned and reviewed before deployment. No remote changes were performed.'
