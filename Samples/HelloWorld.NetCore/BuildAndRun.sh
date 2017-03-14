#/bin/bash

dotnet restore
dotnet build

# Run the 2 console apps in different windows

dotnet run --project ./src/SiloHost &
sleep 10
dotnet run --project ./src/OrleansClient &