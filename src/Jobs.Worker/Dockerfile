# Build context: repository root (see docker-compose.yml).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Jobs.Core/Jobs.Core.csproj src/Jobs.Core/
COPY src/Jobs.Worker/Jobs.Worker.csproj src/Jobs.Worker/
RUN dotnet restore src/Jobs.Worker/Jobs.Worker.csproj

COPY src/Jobs.Core/ src/Jobs.Core/
COPY src/Jobs.Worker/ src/Jobs.Worker/
RUN dotnet publish src/Jobs.Worker/Jobs.Worker.csproj --configuration Release --no-restore --output /app

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS final
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "Jobs.Worker.dll"]
