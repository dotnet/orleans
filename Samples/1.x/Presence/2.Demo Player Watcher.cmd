@setlocal
@echo off
@if NOT "%ECHO%"=="" @echo %ECHO%

set CMDHOME=%~dp0.

cd "%CMDHOME%\PlayerWatcher\bin\Debug"

"%CMDHOME%\PlayerWatcher\bin\Debug\PresencePlayerWatcher.exe"
