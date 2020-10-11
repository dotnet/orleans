#/bin/bash

dotnet restore HelloWorld.Build.sln
dotnet build HelloWorld.Build.sln --no-restore
cd src/OrleansClientCpp
./buildOSX.sh
chmod +x bin/osx/ClientCpp
cd ../../

# Run the 2 console apps in different windows

dotnet run --project ./src/SiloHost --no-build &
sleep 10
cd src/OrleansClientCpp/bin/osx/
./ClientCpp
cd ../../
killall dotnet