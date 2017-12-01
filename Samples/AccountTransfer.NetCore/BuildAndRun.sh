#/bin/bash

dotnet restore
dotnet build --no-restore

# Run the 2 console apps in different windows

dotnet run --project ./src/SiloHost --no-build & 
sleep 10
dotnet run --project ./src/OrleansClient --no-build &