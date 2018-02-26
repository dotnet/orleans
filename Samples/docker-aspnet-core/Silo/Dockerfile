FROM microsoft/aspnetcore:2.0 AS base
WORKDIR /app

FROM microsoft/aspnetcore-build:2.0 AS build
WORKDIR /src
COPY *.sln ./
COPY NuGet.Config ./
COPY Silo/Silo.csproj Silo/
RUN dotnet restore
COPY . .
WORKDIR /src/Silo
RUN dotnet build -c Release -o /app

FROM build AS publish
RUN dotnet publish -c Release -o /app

FROM base AS final
WORKDIR /app
COPY --from=publish /app .
COPY connection_string.txt .
ENTRYPOINT ["dotnet", "Silo.dll"]
