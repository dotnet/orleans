@echo off
echo === Adventure Sample ===
echo This sample requires running both server and client.
echo.
echo Starting server...
start "Adventure Server" cmd /k dotnet run --project "%~dp0AdventureServer\AdventureServer.csproj"
echo Waiting for server to start...
timeout /t 5 /nobreak >nul
echo Starting client...
dotnet run --project "%~dp0AdventureClient\AdventureClient.csproj"
