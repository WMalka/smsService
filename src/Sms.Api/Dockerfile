# Build context is the repository root (see docker-compose.yml).
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore layer: only project files, so it is cached until a csproj changes.
COPY Directory.Build.props ./
COPY src/Sms.Core/Sms.Core.csproj src/Sms.Core/
COPY src/Sms.Infrastructure/Sms.Infrastructure.csproj src/Sms.Infrastructure/
COPY src/Sms.Api/Sms.Api.csproj src/Sms.Api/
RUN dotnet restore src/Sms.Api/Sms.Api.csproj

COPY src/ src/
RUN dotnet publish src/Sms.Api/Sms.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/publish .
COPY seed/ ./seed/
# /data holds the SQLite file (named volume); owned by the non-root user the image provides.
RUN mkdir /data && chown $APP_UID /data
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Sms.Api.dll"]
