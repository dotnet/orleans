@echo off
echo === ChatRoom Sample ===
echo This sample requires running both service and client.
echo.
echo Starting service...
start "ChatRoom Service" cmd /k dotnet run --project "%~dp0ChatRoom.Service\ChatRoom.Service.csproj"
echo Waiting for service to start...
timeout /t 5 /nobreak >nul
echo Starting client...
dotnet run --project "%~dp0ChatRoom.Client\ChatRoom.Client.csproj"
