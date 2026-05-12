@echo off
setlocal EnableExtensions DisableDelayedExpansion

set "REPO_ROOT=%~dp0.."
for %%I in ("%REPO_ROOT%") do set "REPO_ROOT=%%~fI"

set "BIN_DIR=%LOCALAPPDATA%\EvoLoopAgent\bin"
if not exist "%BIN_DIR%" mkdir "%BIN_DIR%" >nul 2>nul
if errorlevel 1 exit /b 1

(
  echo @echo off
  echo call "%REPO_ROOT%\scripts\run-agent.cmd" --workspace "%%cd%%" %%*
) > "%BIN_DIR%\agent.cmd"
if errorlevel 1 exit /b 1

(
  echo @echo off
  echo call "%REPO_ROOT%\scripts\run-agent-cli.cmd" --workspace "%%cd%%" %%*
) > "%BIN_DIR%\agent-cli.cmd"
if errorlevel 1 exit /b 1

set "USER_PATH="
for /f "skip=2 tokens=1,2,*" %%A in ('reg query HKCU\Environment /v Path 2^>nul') do (
  if /I "%%A"=="Path" set "USER_PATH=%%C"
)

if defined USER_PATH (
  for %%P in ("%USER_PATH:;=" "%") do (
    if /I "%%~P"=="%BIN_DIR%" (
      echo User PATH already contains: %BIN_DIR%
      echo Installed wrappers:
      echo   %BIN_DIR%\agent.cmd
      echo   %BIN_DIR%\agent-cli.cmd
      echo Open a new terminal, then run:
      echo   agent
      echo   agent-cli doctor
      exit /b 0
    )
  )
  set "NEW_PATH=%USER_PATH%;%BIN_DIR%"
) else (
  set "NEW_PATH=%BIN_DIR%"
)

reg add HKCU\Environment /v Path /t REG_EXPAND_SZ /d "%NEW_PATH%" /f >nul
if errorlevel 1 exit /b 1

echo Added to user PATH: %BIN_DIR%
echo Installed wrappers:
echo   %BIN_DIR%\agent.cmd
echo   %BIN_DIR%\agent-cli.cmd
echo Open a new terminal, then run:
echo   agent
echo   agent-cli doctor
endlocal
