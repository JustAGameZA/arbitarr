# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Arbitarr.Host/Arbitarr.Host.csproj -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# Runtime state (SQLite cache, fetched datasets) lives under /config,
# mounted from the host (e.g. Unraid appdata).
ENV ARBITARR__CONFIGDIR=/config \
    ASPNETCORE_URLS=http://+:8080
VOLUME /config
EXPOSE 8080

ENTRYPOINT ["dotnet", "Arbitarr.Host.dll"]
