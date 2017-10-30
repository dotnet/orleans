# Prerequisites

You need to have installed:
 - dotnet core sdk
 - docker engine and docker-compose
 - a text file named "connection-string.txt" at the solution root containing a connection string to an azure storage account

# Build

Just run the script Build.cmd

It will build the app using the dotnet command and create the docker container using the docker-compose command

# Launch

To start a silo, just type in this folder:

docker-compose up -d silo

Before starting the client, wait a little bit so the silo is up and running. You can check the logs from the silo with this command:

docker-compose logs silo

Once you see in the logs "Silo started", you can launch the client like this

docker-compose run client

By using "run" instead of "start" you will be able to see directly the client console output.
