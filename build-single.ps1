$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
$previousPackages = $env:NUGET_PACKAGES
$previousHttpCache = $env:NUGET_HTTP_CACHE_PATH
try {
    # Keep downloaded packages and caches inside this project.
    $env:NUGET_PACKAGES = Join-Path $PSScriptRoot 'artifacts\nuget-packages'
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $PSScriptRoot 'artifacts\nuget-http-cache'
    $stage = Join-Path $PSScriptRoot ('artifacts\single-build-' + [Guid]::NewGuid().ToString('N'))
    dotnet publish ClaudeUsageWidget --configuration Release --runtime win-x64 --self-contained true `
        -p:PublishProfile=SingleFile -p:DebugType=embedded -p:NuGetAudit=false `
        --source https://api.nuget.org/v3/index.json --output $stage
    if ($LASTEXITCODE -ne 0) { throw 'Single-file publish failed.' }

    $files = @(Get-ChildItem -LiteralPath $stage -File -Recurse)
    if ($files.Count -ne 1 -or $files[0].Name -ne 'ClaudeUsageWidget.exe') {
        throw 'Publish output is not exactly one executable. Inspect the staging folder.'
    }
    $destination = Join-Path $PSScriptRoot 'artifacts\standalone'
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    $executable = Join-Path $destination 'ClaudeUsageWidget.exe'
    Copy-Item -LiteralPath $files[0].FullName -Destination $executable -Force
    Write-Output ('Single EXE: ' + $executable)
    Write-Output ('Size: {0:N1} MiB' -f ((Get-Item -LiteralPath $executable).Length / 1MB))

    $resolvedStage = [IO.Path]::GetFullPath($stage)
    $allowedRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts')) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedStage.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing cleanup outside the project artifacts folder.'
    }
    Remove-Item -LiteralPath $resolvedStage -Recurse -Force
} finally {
    $env:NUGET_PACKAGES = $previousPackages
    $env:NUGET_HTTP_CACHE_PATH = $previousHttpCache
    Pop-Location
}
