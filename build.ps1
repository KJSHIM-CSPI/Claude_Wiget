param([string]$Output = 'artifacts/app')
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    foreach ($project in @('ClaudeUsageWidget', 'ClaudeUsageWidget.Tests', 'ClaudeUsageWidget.NativeHost')) {
        dotnet restore $project --source . -p:NuGetAudit=false
        if ($LASTEXITCODE -ne 0) { throw "Restore failed: $project" }
    }
    dotnet run --project ClaudeUsageWidget.Tests --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Core checks failed.' }
    node --test ClaudeUsageWidget.Tests/browser.test.cjs ClaudeUsageWidget.Tests/extension.test.mjs
    if ($LASTEXITCODE -ne 0) { throw 'Browser checks failed.' }
    dotnet publish ClaudeUsageWidget --configuration Release --no-restore --self-contained false --output $Output
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    dotnet publish ClaudeUsageWidget.NativeHost --configuration Release --no-restore --self-contained false --output (Join-Path $Output 'native-host')
    if ($LASTEXITCODE -ne 0) { throw 'Host publish failed.' }
    Copy-Item -LiteralPath 'ClaudeUsageWidget.NativeHost/com.claude_usage_widget.bridge.json' -Destination (Join-Path $Output 'native-host')
    New-Item -ItemType Directory -Path (Join-Path $Output 'extension') -Force | Out-Null
    Get-ChildItem -LiteralPath 'extension' -File | Copy-Item -Destination (Join-Path $Output 'extension')
    Copy-Item -LiteralPath 'setup-browser.ps1','setup-browser.cmd' -Destination $Output
    Copy-Item -LiteralPath 'BROWSER-SETUP.md','AUTHENTICATION.md' -Destination $Output
} finally { Pop-Location }
