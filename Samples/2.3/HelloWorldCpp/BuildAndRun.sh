#/bin/bash

dotnet restore HelloWorld.Build.sln
dotnet build HelloWorld.Build.sln --no-restore
cd src/OrleansClientCpp
./build.sh
chmod +x bin/linux/ClientCpp
cd ../../

# Run the 2 console apps in different windows

dotnet run --project ./src/SiloHost --no-build &
sleep 10
cd src/OrleansClientCpp/bin/linux/
./ClientCpp
cd ../../
killall dotnet