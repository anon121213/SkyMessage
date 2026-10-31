# Build context: repository root (see docker-compose.yml).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# true = API without the bundled web client (quick server-only images).
ARG SKIP_WEB_CLIENT=false

# Restore first for layer caching (global.json is not copied: the image's SDK may be a different band).
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Shared/SkyMessage.Contracts/SkyMessage.Contracts.csproj src/Shared/SkyMessage.Contracts/
COPY src/Server/SkyMessage.Domain/SkyMessage.Domain.csproj src/Server/SkyMessage.Domain/
COPY src/Server/SkyMessage.Application/SkyMessage.Application.csproj src/Server/SkyMessage.Application/
COPY src/Server/SkyMessage.Infrastructure/SkyMessage.Infrastructure.csproj src/Server/SkyMessage.Infrastructure/
COPY src/Server/SkyMessage.Api/SkyMessage.Api.csproj src/Server/SkyMessage.Api/
COPY src/Client/SkyMessage.Client.Core/SkyMessage.Client.Core.csproj src/Client/SkyMessage.Client.Core/
COPY src/Client/SkyMessage.Client.UI/SkyMessage.Client.UI.csproj src/Client/SkyMessage.Client.UI/
COPY src/Client/SkyMessage.Web/SkyMessage.Web.csproj src/Client/SkyMessage.Web/
RUN dotnet restore src/Server/SkyMessage.Api/SkyMessage.Api.csproj -p:SkipWebClient=${SKIP_WEB_CLIENT}

COPY src/ src/
RUN dotnet publish src/Server/SkyMessage.Api/SkyMessage.Api.csproj -c Release -o /app/publish --no-restore \
    -p:UseAppHost=false -p:SkipWebClient=${SKIP_WEB_CLIENT}

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS aspnet

# Runtime on Debian 13: its ffmpeg 7.1 decodes tiled iPhone HEIC photos (Ubuntu 24.04's 6.1, the base of the
# official aspnet image, cannot). The .NET runtime is copied from the official image.
FROM debian:trixie-slim AS runtime
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates tzdata libicu76 libssl3t64 libstdc++6 zlib1g ffmpeg \
    && rm -rf /var/lib/apt/lists/* \
    && groupadd --gid 1654 app && useradd --uid 1654 --gid app --create-home app
COPY --from=aspnet /usr/share/dotnet /usr/share/dotnet
RUN ln -s /usr/share/dotnet/dotnet /usr/bin/dotnet
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_gcServer=0
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "SkyMessage.Api.dll"]
