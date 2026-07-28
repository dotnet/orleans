@echo off
echo === Chirper Sample ===
echo This sample requires running both server and client.
echo.
echo Option 1: Run server only (open another terminal for client)
echo Option 2: Run client only (requires server running)
echo.
echo To run server: dotnet run --project Chirper.Server\Chirper.Server.csproj
echo To run client: dotnet run --project Chirper.Client\Chirper.Client.csproj
echo.
echo Starting server...
start "Chirper Server" cmd /k dotnet run --project "%~dp0Chirper.Server\Chirper.Server.csproj"
echo Waiting for server to start...
timeout /t 5 /nobreak >nul
echo Starting client...
dotnet run --project "%~dp0Chirper.Client\Chirper.Client.csproj"
