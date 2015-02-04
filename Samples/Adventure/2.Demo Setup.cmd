@setlocal
@echo off
@if NOT "%ECHO%"=="" @echo %ECHO%

set CMDHOME=%~dp0.

if exist "%CMDHOME%\AdventureSetup\bin\Debug\AdventureSetup.exe" (
cd "%CMDHOME%\AdventureSetup\bin\Debug"
"%CMDHOME%\AdventureSetup\bin\Debug\AdventureSetup.exe" %1
cd "%CMDHOME%"
) else (
@echo Build Adventure.sln and then run the program
)