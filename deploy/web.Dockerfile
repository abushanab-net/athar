# Athar web (Blazor UI, REST, MCP, OAuth, share pages, internal collab API). Build from the repository root:
#   docker build -f deploy/web.Dockerfile -t athar-web .

# The editor island bundle (src/editor) is a build output the web project serves from wwwroot/editor.
FROM node:22-bookworm-slim AS editor
WORKDIR /src/editor
COPY src/editor/package.json src/editor/package-lock.json ./
RUN npm ci
COPY src/editor/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Packages.props Athar.slnx ./
COPY src/ src/
COPY docs/handbook/ docs/handbook/
COPY agent-skills/ agent-skills/
COPY agent-plugin/ agent-plugin/
COPY --from=editor /src/Athar.Web/wwwroot/editor/ src/Athar.Web/wwwroot/editor/
# The bundle can come from a cached layer older than the checkout; touch it so MSBuild's BuildEditor target (npm) is up to date.
RUN touch src/Athar.Web/wwwroot/editor/*
RUN dotnet publish src/Athar.Web -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# curl for the container health check; the data folders are the persistent volumes (keys and attachments, shared with the worker).
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /data/keys /data/attachments && chown -R app:app /data
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_FORWARDEDHEADERS_ENABLED=true \
    Athar__Keys__Path=/data/keys \
    Athar__Attachments__Path=/data/attachments
USER app
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=5s --start-period=60s --retries=5 CMD curl -fsS http://localhost:8080/health || exit 1
ENTRYPOINT ["dotnet", "Athar.Web.dll"]
