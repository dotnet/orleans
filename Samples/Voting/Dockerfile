#See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/aspnet:5.0 AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443
EXPOSE 11111
EXPOSE 8888

FROM mcr.microsoft.com/dotnet/sdk:5.0 AS build
WORKDIR /src
COPY ["Voting.csproj", "."]
RUN dotnet restore "./Voting.csproj"
COPY . .
WORKDIR "/src/."
RUN dotnet build "Voting.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "Voting.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Voting.dll"]