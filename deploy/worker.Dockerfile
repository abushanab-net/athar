# Athar worker (embed, extract, GitHub sync, upload text, retention, transcript purge). Build from the repository root:
#   docker build -f deploy/worker.Dockerfile -t athar-worker .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Packages.props Athar.slnx ./
COPY src/ src/
COPY docs/handbook/ docs/handbook/
RUN dotnet publish src/Athar.Worker -c Release -o /app

# The ASP.NET runtime: the worker shares the Data Protection key ring with web.
FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN mkdir -p /data/keys /data/attachments && chown -R app:app /data
WORKDIR /app
COPY --from=build /app .
ENV Athar__Keys__Path=/data/keys \
    Athar__Attachments__Path=/data/attachments
USER app
ENTRYPOINT ["dotnet", "Athar.Worker.dll"]
