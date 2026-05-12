@echo off
setlocal
set ROUTE=tui
call :selectRoute %*
if /I "%ROUTE%"=="cli" (
  "%~dp0Agent.Cli.exe" --workspace "%cd%" %*
) else (
  "%~dp0Agent.Tui.exe" --workspace "%cd%" %*
)
exit /b %errorlevel%

:selectRoute
if "%~1"=="" exit /b 0
if /I "%~1"=="--workspace" (
  shift
  shift
  goto selectRoute
)
if /I "%~1"=="--config" (
  shift
  shift
  goto selectRoute
)
if /I "%~1"=="--profile" (
  shift
  shift
  goto selectRoute
)
if /I "%~1"=="--model" (
  shift
  shift
  goto selectRoute
)
if /I "%~1"=="--no-color" (
  shift
  goto selectRoute
)
if /I "%~1"=="--offline-strict" (
  shift
  goto selectRoute
)
if /I "%~1"=="doctor" set ROUTE=cli
if /I "%~1"=="run" set ROUTE=cli
if /I "%~1"=="plan" set ROUTE=cli
if /I "%~1"=="review" set ROUTE=cli
if /I "%~1"=="repl" set ROUTE=cli
if /I "%~1"=="interactive" set ROUTE=cli
exit /b 0
