FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443
EXPOSE 11111
ENV ASPNETCORE_HTTP_PORTS=80

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore "samples/Deployment/AzureContainerApps/Scaler/Scaler.csproj" --configfile samples/NuGet.config
WORKDIR "/src/samples/Deployment/AzureContainerApps/Scaler"
RUN dotnet build "Scaler.csproj" -c Release -o /app/build --no-restore

FROM build AS publish
RUN dotnet publish "Scaler.csproj" -c Release -o /app/publish --no-restore

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Scaler.dll"]