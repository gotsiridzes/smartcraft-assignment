# One-shot schema migration image: an EF Core migrations bundle built with the pinned dotnet-ef
# tool (dotnet-tools.json). It applies pending migrations and exits; the app images contain no
# migration code. Build context: repository root (see docker-compose.yml).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props dotnet-tools.json ./
RUN dotnet tool restore

COPY src/Jobs.Core/Jobs.Core.csproj src/Jobs.Core/
COPY src/Jobs.Api/Jobs.Api.csproj src/Jobs.Api/
RUN dotnet restore src/Jobs.Api/Jobs.Api.csproj

COPY src/Jobs.Core/ src/Jobs.Core/
COPY src/Jobs.Api/ src/Jobs.Api/
RUN dotnet ef migrations bundle \
        --project src/Jobs.Core \
        --startup-project src/Jobs.Api \
        --configuration Release \
        --output /out/efbundle \
        --force

# The startup project is a web app, so the framework-dependent bundle needs the ASP.NET Core runtime.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /out/efbundle .
USER $APP_UID
ENTRYPOINT ["./efbundle"]
