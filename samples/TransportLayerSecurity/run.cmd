@echo off
echo === TransportLayerSecurity Sample ===
echo This sample demonstrates TLS communication between client and server.
echo.
echo Starting server...
start "TLS Server" cmd /k dotnet run --project "%~dp0TLS.Server\TLS.Server.csproj"
echo Waiting for server to start...
timeout /t 5 /nobreak >nul
echo Starting client...
dotnet run --project "%~dp0TLS.Client\TLS.Client.csproj"
