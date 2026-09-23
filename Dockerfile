FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
ARG TARGETARCH=amd64

COPY global.json RemoveBg.slnx ./
COPY src/RemoveBg.Api/RemoveBg.Api.csproj src/RemoveBg.Api/
RUN case "$TARGETARCH" in \
        amd64) dotnet_arch=x64 ;; \
        arm64) dotnet_arch=arm64 ;; \
        *) echo "Unsupported Docker architecture: $TARGETARCH" >&2; exit 1 ;; \
    esac \
    && dotnet restore src/RemoveBg.Api/RemoveBg.Api.csproj \
        --runtime "linux-$dotnet_arch"

COPY src/RemoveBg.Api src/RemoveBg.Api
RUN case "$TARGETARCH" in \
        amd64) dotnet_arch=x64 ;; \
        arm64) dotnet_arch=arm64 ;; \
        *) echo "Unsupported Docker architecture: $TARGETARCH" >&2; exit 1 ;; \
    esac \
    && dotnet publish src/RemoveBg.Api/RemoveBg.Api.csproj \
        --configuration Release \
        --runtime "linux-$dotnet_arch" \
        --self-contained false \
        --output /app/publish \
        --no-restore \
        -p:UseAppHost=false

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
