#!/bin/bash
echo "Building and running Blazor WebAssembly sample..."
echo "Open https://localhost:5001 in your browser"
dotnet run --project "$(dirname "$0")/BlazorWasm.Server/BlazorWasm.Server.csproj"
