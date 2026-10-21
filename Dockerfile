# Build the Vue client independently from the .NET API and trading engines.
FROM node:22 AS frontend
WORKDIR /src/ForexHitl.Web
COPY src/ForexHitl.Web/package.json src/ForexHitl.Web/package-lock.json ./
RUN npm ci
COPY src/ForexHitl.Web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/ ./src/
COPY --from=frontend /src/ForexHitl/wwwroot/ ./src/ForexHitl/wwwroot/

# Server (API + background scanner)
RUN dotnet publish src/ForexHitl/ForexHitl.csproj -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
# Fail the image build, not the first start, if the runtime image lacks the timezone data trading hours need.
RUN test -f /usr/share/zoneinfo/Australia/Melbourne
COPY --from=build /out ./

# Render's default web service port. Persistent state goes to /app/data (mount a disk there).
ENV ASPNETCORE_URLS=http://+:10000 \
    GUARD_STATE_PATH=/app/data/guard-state.json
EXPOSE 10000

ENTRYPOINT ["dotnet", "ForexHitl.dll"]
