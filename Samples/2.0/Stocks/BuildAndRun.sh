#/bin/bash

dotnet restore
dotnet build --no-restore

# Run the 2 console apps in different windows

dotnet run --project ./src/SiloHost --no-build
