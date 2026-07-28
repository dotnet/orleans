#!/bin/bash
echo "Building and running ShoppingCart sample..."
echo "Open the URL shown by the application in your browser"
dotnet run --project "$(dirname "$0")/Silo/Orleans.ShoppingCart.Silo.csproj" --environment ASPNETCORE_ENVIRONMENT=Development
