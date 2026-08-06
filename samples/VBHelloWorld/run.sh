#!/bin/bash
echo "Building and running VBHelloWorld sample..."
dotnet run --project "$(dirname "$0")/HelloWorld/HelloWorld.csproj"
