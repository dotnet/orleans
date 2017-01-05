@setlocal
@echo off
@if NOT "%ECHO%"=="" @echo %ECHO%

set CMDHOME=%~dp0.

if exist "%CMDHOME%\AdventureSocketClient\bin\Debug\AdventureSocketClient.exe" (
cd /d "%CMDHOME%\AdventureSocketClient\bin\Debug"
AdventureSocketClient.exe
cd "%CMDHOME%"
) else (
@echo Build Adventure.sln and then run the program
)