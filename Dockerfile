FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json RemoveBg.slnx ./
COPY src/RemoveBg.Api/RemoveBg.Api.csproj src/RemoveBg.Api/
RUN dotnet restore src/RemoveBg.Api/RemoveBg.Api.csproj

COPY src/RemoveBg.Api src/RemoveBg.Api
RUN dotnet publish src/RemoveBg.Api/RemoveBg.Api.csproj \
    --configuration Release \
    --output /app/publish \
    --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
USER root
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /data /models \
    && chown -R app:app /data /models

WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8000 \
    DOTNET_EnableDiagnostics=0

USER app
EXPOSE 8000
ENTRYPOINT ["dotnet", "RemoveBg.Api.dll"]
