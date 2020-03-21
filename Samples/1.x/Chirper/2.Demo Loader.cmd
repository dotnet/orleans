@setlocal
@echo off
@if NOT "%ECHO%"=="" @echo %ECHO%

set CMDHOME=%~dp0.

set DATADIR=%CMDHOME%\NetworkLoader\bin\Debug\GraphData
set DATAFILE=Network-1000nodes-27000edges.graphml

if exist "%CMDHOME%\NetworkLoader\bin\Debug\Chirper.NetworkLoader.exe" (
cd "%CMDHOME%\NetworkLoader\bin\Debug"
"%CMDHOME%\NetworkLoader\bin\Debug\Chirper.NetworkLoader.exe" "%DATADIR%\%DATAFILE%"
) else (
@echo Build Chirper.sln and then run the program
)

pause