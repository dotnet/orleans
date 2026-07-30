#!/bin/bash
echo "Building and running FSharpHelloWorld sample..."
dotnet run --project "$(dirname "$0")/HelloWorld/HelloWorld.csproj"
