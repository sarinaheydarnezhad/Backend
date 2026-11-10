param(
    [switch]$CheckConfiguration,
    [switch]$CheckProvider,
    [string]$EnvironmentFile = (Join-Path $PSScriptRoot '..\.env')
)
$ErrorActionPreference = 'Stop'
$repositoryPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$localEnvironmentPath = [IO.Path]::GetFullPath($EnvironmentFile)
if (-not (Test-Path -LiteralPath $localEnvironmentPath -PathType Leaf)) {
    throw 'Local Kavenegar environment file is missing.'
}
# Parse data only: never evaluate a dotenv file as PowerShell code or print values.
$lineNumber = 0
foreach ($line in [IO.File]::ReadAllLines($localEnvironmentPath)) {
    $lineNumber++
    if ([string]::IsNullOrWhiteSpace($line) -or $line.TrimStart().StartsWith('#')) { continue }
    if ($line -notmatch '^\s*((?:Sms|Kavenegar|Otp)__[A-Za-z0-9_]+)\s*=(.*)$') {
        throw "Unsupported local SMS setting on line $lineNumber."
    }
    $settingName = $Matches[1]
    $settingValue = $Matches[2].Trim()
    if ($settingValue.Length -ge 2 -and
        (($settingValue.StartsWith('"') -and $settingValue.EndsWith('"')) -or
         ($settingValue.StartsWith("'") -and $settingValue.EndsWith("'")))) {
        $settingValue = $settingValue.Substring(1, $settingValue.Length - 2)
    }
    [Environment]::SetEnvironmentVariable($settingName, $settingValue, 'Process')
}
$env:DOTNET_CLI_HOME = Join-Path $repositoryPath '.dotnet'
$env:NUGET_PACKAGES = Join-Path $repositoryPath '.nuget\packages'
Push-Location $repositoryPath
try {
    if ($CheckConfiguration -or $CheckProvider) {
        $checkArguments = @('run', '--no-restore', '--no-launch-profile', '--project', 'backend/Lerona.SmsCheck/Lerona.SmsCheck.csproj', '--')
        if ($CheckProvider) { $checkArguments += '--provider-check' }
        & dotnet @checkArguments
    } else {
        # Existing Auth/database/backup/Cent settings must already be supplied separately.
        & dotnet run --no-launch-profile --project backend/Flashcards.Api/Flashcards.Api.csproj
    }
    $commandExitCode = $LASTEXITCODE
} finally { Pop-Location }
exit $commandExitCode
