@echo off
echo Building and running Voting sample...
echo Open https://localhost:7178 in your browser
echo Orleans Dashboard available at https://localhost:7178/dashboard/
dotnet run --project "%~dp0Voting.csproj"
