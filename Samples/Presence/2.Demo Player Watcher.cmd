@setlocal
@echo off
@if NOT "%ECHO%"=="" @echo %ECHO%

set CMDHOME=%~dp0.

"%CMDHOME%\PlayerWatcher\bin\Debug\PresencePlayerWatcher.exe"
