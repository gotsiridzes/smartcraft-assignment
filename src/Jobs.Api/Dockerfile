# Build context: repository root (see docker-compose.yml).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Jobs.Core/Jobs.Core.csproj src/Jobs.Core/
COPY src/Jobs.Api/Jobs.Api.csproj src/Jobs.Api/
RUN dotnet restore src/Jobs.Api/Jobs.Api.csproj

COPY src/Jobs.Core/ src/Jobs.Core/
COPY src/Jobs.Api/ src/Jobs.Api/
RUN dotnet publish src/Jobs.Api/Jobs.Api.csproj --configuration Release --no-restore --output /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Jobs.Api.dll"]
