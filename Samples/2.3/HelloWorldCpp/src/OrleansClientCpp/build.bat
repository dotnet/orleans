@echo off

REM This script builds the sample for x64 Windows 10. If using Win7, adjust the 'dotnet publish' command.
REM It assumes that both the dotnet CLI and cl.exe compiler are available on the path.
REM A Visual Studio Developer Command Prompt will already have cl.exe on its path.

SET SRCDIR=%~dp0
SET OUTDIR=%~dp0bin\windows

mkdir %OUTDIR%

REM Build managed component
echo Building Orleans TestClient Managed
dotnet publish --self-contained -r win10-x64 %SRCDIR%\TestClient\TestClient.csproj -o %OUTDIR%

REM Build native component
cl.exe %SRCDIR%\client.cpp /Fo%OUTDIR%\ /Fd%OUTDIR%\client.pdb /EHsc /Od /GS /sdl /Zi /D "WINDOWS" /link ole32.lib /out:%OUTDIR%\ClientCpp.exe