@echo off
setlocal EnableExtensions DisableDelayedExpansion

set "BUNDLE_DIR=%~dp0"
if "%BUNDLE_DIR:~-1%"=="\" set "BUNDLE_DIR=%BUNDLE_DIR:~0,-1%"

for %%F in (Agent.Tui.exe Agent.Cli.exe agent.cmd agent-cli.cmd) do (
  if not exist "%BUNDLE_DIR%\%%F" (
    echo Required bundle file not found: "%BUNDLE_DIR%\%%F" >&2
    exit /b 1
  )
)

set "USER_PATH="
for /f "skip=2 tokens=1,2,*" %%A in ('reg query HKCU\Environment /v Path 2^>nul') do (
  if /I "%%A"=="Path" set "USER_PATH=%%C"
)

if defined USER_PATH (
  for %%P in ("%USER_PATH:;=" "%") do (
    if /I "%%~P"=="%BUNDLE_DIR%" (
      echo User PATH already contains: %BUNDLE_DIR%
      echo Open a new terminal, then run:
      echo   agent
      echo   agent-cli doctor
      exit /b 0
    )
  )
  set "NEW_PATH=%USER_PATH%;%BUNDLE_DIR%"
) else (
  set "NEW_PATH=%BUNDLE_DIR%"
)

reg add HKCU\Environment /v Path /t REG_EXPAND_SZ /d "%NEW_PATH%" /f >nul
if errorlevel 1 exit /b 1

echo Added to user PATH: %BUNDLE_DIR%
echo Open a new terminal, then run:
echo   agent
echo   agent-cli doctor
endlocal
