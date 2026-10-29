# Build context is the repo root (see docker-compose.yml).
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY backend/Directory.Build.props ./
COPY backend/src/Crawler.Domain/Crawler.Domain.csproj src/Crawler.Domain/
COPY backend/src/Crawler.Infrastructure/Crawler.Infrastructure.csproj src/Crawler.Infrastructure/
COPY backend/src/Crawler.Worker/Crawler.Worker.csproj src/Crawler.Worker/
RUN dotnet restore src/Crawler.Worker/Crawler.Worker.csproj

COPY backend/src/ src/
RUN dotnet publish src/Crawler.Worker/Crawler.Worker.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends wget && rm -rf /var/lib/apt/lists/*
COPY --from=build /app .
EXPOSE 8081
USER $APP_UID
ENTRYPOINT ["dotnet", "Crawler.Worker.dll"]
