param([string]$Output = 'artifacts/app')
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    foreach ($project in @('ClaudeUsageWidget', 'ClaudeUsageWidget.Tests')) {
        dotnet restore $project --source . -p:NuGetAudit=false
        if ($LASTEXITCODE -ne 0) { throw "Restore failed: $project" }
    }
    dotnet run --project ClaudeUsageWidget.Tests --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Core checks failed.' }
    dotnet publish ClaudeUsageWidget --configuration Release --no-restore --self-contained false --output $Output
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Copy-Item -LiteralPath 'AUTHENTICATION.md' -Destination $Output
} finally { Pop-Location }
