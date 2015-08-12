@setlocal
@echo off
@if NOT "%ECHO%"=="" @echo %ECHO%

SET CMDHOME=%~dp0.
"%CMDHOME%\AdventureSetup\bin\Debug\AdventureSetup.exe"

if exist "%CMDHOME%\AdventureSetup\bin\Debug\AdventureSetup.exe" (
cd "%CMDHOME%\AdventureSetup\bin\Debug"
AdventureSetup.exe
cd "%CMDHOME%"
) else (
@echo Build Adventure.sln and then run the program
)