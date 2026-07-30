@echo off
echo === Presence Sample ===
echo This sample has 3 components:
echo   - PresenceService: The Orleans silo
echo   - LoadGenerator: Generates simulated player activity
echo   - PlayerWatcher: Watches player presence changes
echo.
echo Starting PresenceService...
start "Presence Service" cmd /k dotnet run --project "%~dp0src\PresenceService\PresenceService.csproj"
echo Waiting for service to start...
timeout /t 5 /nobreak >nul
echo Starting LoadGenerator...
start "Load Generator" cmd /k dotnet run --project "%~dp0src\LoadGenerator\LoadGenerator.csproj"
echo Starting PlayerWatcher...
dotnet run --project "%~dp0src\PlayerWatcher\PlayerWatcher.csproj"
