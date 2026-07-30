#!/bin/bash
echo "Building and running HelloWorld sample..."
dotnet run --project "$(dirname "$0")/HelloWorld.csproj"
