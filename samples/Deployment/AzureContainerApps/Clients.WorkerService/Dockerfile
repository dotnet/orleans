FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080
USER $APP_UID

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore "samples/Deployment/AzureContainerApps/Clients.WorkerService/Clients.WorkerService.csproj" --configfile samples/NuGet.config
WORKDIR "/src/samples/Deployment/AzureContainerApps/Clients.WorkerService"
RUN dotnet build "Clients.WorkerService.csproj" -c Release -o /app/build --no-restore

FROM build AS publish
RUN dotnet publish "Clients.WorkerService.csproj" -c Release -o /app/publish --no-restore

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Clients.WorkerService.dll"]