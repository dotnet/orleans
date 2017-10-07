#/bin/bash

dotnet restore
dotnet publish

# Run the 2 console apps in different windows

dotnet ./src/SiloHost/bin/Debug/netcoreapp2.0/publish/SiloHost.dll & 
sleep 10
dotnet ./src/OrleansClient/bin/Debug/netcoreapp2.0/publish/OrleansClient.dll &