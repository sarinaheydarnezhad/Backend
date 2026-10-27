[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repository = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$stage = Join-Path $repository ('.dotnet/publish-' + [Guid]::NewGuid().ToString('N'))
$destination = Join-Path $repository 'backend/publish'
$project = Join-Path $repository 'backend/Flashcards.Api/Flashcards.Api.csproj'

& dotnet publish $project --no-restore --disable-build-servers -c Release -m:1 -p:UseSharedCompilation=false -o $stage --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw 'Backend publication failed; existing publish files were not copied.' }

foreach ($file in Get-ChildItem -LiteralPath $stage -Recurse -File) {
    $relative = $file.FullName.Substring($stage.Length).TrimStart([char[]]'\/')
    $target = Join-Path $destination $relative
    if ((Test-Path -LiteralPath $target) -and ($file.Name -like 'appsettings*.json' -or $file.Name -eq 'web.config' -or $file.Name -eq 'liara.json')) { continue }
    $directory = Split-Path -Parent $target
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $target -Force
}

Write-Output 'Backend publish assemblies refreshed. Existing deployment settings and Liara configuration were preserved.'
Write-Output 'No deployment or database migration was performed. Review configuration and deploy the complete publish directory together.'
