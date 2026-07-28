@echo off
echo Building and running Blazor WebAssembly sample...
echo Open https://localhost:5001 in your browser
dotnet run --project "%~dp0BlazorWasm.Server\BlazorWasm.Server.csproj"
