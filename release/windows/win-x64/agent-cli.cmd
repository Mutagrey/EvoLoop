@echo off
setlocal
"%~dp0Agent.Cli.exe" --workspace "%cd%" %*
endlocal
