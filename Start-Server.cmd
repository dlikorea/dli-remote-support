@echo off
setlocal
cd /d "%~dp0"
set "DLI_NODE="
for /f "delims=" %%N in ('where node.exe 2^>nul') do if not defined DLI_NODE set "DLI_NODE=%%N"
if not defined DLI_NODE (
  for /f "usebackq delims=" %%N in (`%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -Command "Get-AppxPackage -Name 'OpenAI.CodexPrimaryRuntime*' | Sort-Object Version -Descending | Select-Object -First 1 | ForEach-Object { Join-Path $_.InstallLocation 'dependencies\node\bin\node.exe' }"`) do set "DLI_NODE=%%N"
)
if not defined DLI_NODE (
  echo Node.js 24+ is required on the support server PC.
  echo https://nodejs.org/en/download/
  pause
  exit /b 1
)
"%DLI_NODE%" server.js
if errorlevel 1 pause
