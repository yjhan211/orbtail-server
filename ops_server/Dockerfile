# ─── build stage ──────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ops_server/ops_server.csproj ops_server/
RUN dotnet restore ops_server/ops_server.csproj

COPY ops_server/ ops_server/
WORKDIR /src/ops_server
RUN dotnet publish ops_server.csproj -c Release -o /app/publish

# ─── runtime stage ────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

COPY --from=build /app/publish .

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "ops_server.dll"]
