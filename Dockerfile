# Builds the API server and the Blazor WebAssembly client into one image.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/ ./src/

# Server (API + background scanner)
RUN dotnet publish src/ForexHitl/ForexHitl.csproj -c Release -o /out

# Client (static WebAssembly site) -> served by the server from wwwroot
RUN dotnet publish src/ForexHitl.Client/ForexHitl.Client.csproj -c Release -o /client \
 && mkdir -p /out/wwwroot \
 && cp -r /client/wwwroot/. /out/wwwroot/

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /out ./

# Render's default web service port. Persistent state goes to /app/data (mount a disk there).
ENV ASPNETCORE_URLS=http://+:10000 \
    GUARD_STATE_PATH=/app/data/guard-state.json
EXPOSE 10000

ENTRYPOINT ["dotnet", "ForexHitl.dll"]
