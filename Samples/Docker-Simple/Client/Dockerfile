FROM microsoft/dotnet:runtime

WORKDIR /app
COPY publish/* ./

ENTRYPOINT ["dotnet", "Client.dll"]
