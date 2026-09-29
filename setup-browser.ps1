param([switch]$Remove)
$ErrorActionPreference = 'Stop'
$hostName = 'com.claude_usage_widget.bridge'
$registryPath = "HKCU:\Software\Google\Chrome\NativeMessagingHosts\$hostName"
$manifestPath = Join-Path $PSScriptRoot 'native-host\com.claude_usage_widget.bridge.json'
if ($Remove) {
    $entry = Get-Item -LiteralPath $registryPath -ErrorAction SilentlyContinue
    if ($entry -and $entry.GetValue('') -eq $manifestPath) {
        Remove-Item -LiteralPath $registryPath
    }
    Write-Host 'Widget native messaging registration removed.'
    exit
}
$executable = Join-Path $PSScriptRoot 'native-host\ClaudeUsageWidget.NativeHost.exe'
if (!(Test-Path -LiteralPath $executable) -or !(Test-Path -LiteralPath $manifestPath)) { throw 'Build the release package first.' }
# Only this application's per-user Chrome host registration is changed. No browser policy or cookie changes.
New-Item -Path $registryPath -Force | Out-Null
Set-Item -LiteralPath $registryPath -Value $manifestPath
Write-Host 'Registered Claude usage widget for this Windows user. Load the extension folder in chrome://extensions.'
