@echo off
echo === CustomDataAdapter Streaming Sample ===
echo This sample demonstrates custom data adapters for non-Orleans clients.
echo.
echo Starting Silo...
start "Silo" cmd /k dotnet run --project "%~dp0Silo\Silo.csproj"
echo Waiting for silo to start...
timeout /t 5 /nobreak >nul
echo Starting NonOrleansClient...
dotnet run --project "%~dp0NonOrleansClient\NonOrleansClient.csproj"
