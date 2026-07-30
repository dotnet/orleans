@echo off
echo === Simple Streaming Sample ===
echo This sample demonstrates basic Orleans streaming.
echo.
echo Starting SiloHost...
start "Silo Host" cmd /k dotnet run --project "%~dp0SiloHost\SiloHost.csproj"
echo Waiting for silo to start...
timeout /t 5 /nobreak >nul
echo Starting Client...
dotnet run --project "%~dp0Client\Client.csproj"
