#!/bin/bash
echo "Building and running Stocks sample..."
dotnet run --project "$(dirname "$0")/Stocks.csproj"
