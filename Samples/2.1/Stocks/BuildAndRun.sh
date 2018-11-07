#/bin/bash

dotnet restore
dotnet build --no-restore

dotnet run --project ./src/SiloHost --no-build
