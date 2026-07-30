@echo off
echo === BankAccount Sample ===
echo This sample requires running both server and client.
echo.
echo Starting server...
start "Bank Server" cmd /k dotnet run --project "%~dp0BankServer\BankServer.csproj"
echo Waiting for server to start...
timeout /t 5 /nobreak >nul
echo Starting client...
dotnet run --project "%~dp0BankClient\BankClient.csproj"
