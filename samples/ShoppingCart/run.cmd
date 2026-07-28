@echo off
echo Building and running ShoppingCart sample...
echo Open the URL shown by the application in your browser
dotnet run --project "%~dp0Silo\Orleans.ShoppingCart.Silo.csproj" --environment ASPNETCORE_ENVIRONMENT=Development
