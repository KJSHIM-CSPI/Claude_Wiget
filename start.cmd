@echo off
cd /d "%~dp0"
if exist "artifacts\app\ClaudeUsageWidget.exe" (
    start "" "artifacts\app\ClaudeUsageWidget.exe"
) else (
    dotnet run --project "ClaudeUsageWidget\ClaudeUsageWidget.csproj" --configuration Release
    if errorlevel 1 pause
)
