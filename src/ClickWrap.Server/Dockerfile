# See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

# This stage is used when running from VS in fast mode (Default for Debug configuration)
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base

# Switch to root to install curl
USER root

# Install curl
RUN apt-get update && apt-get install -y curl && rm -rf /var/lib/apt/lists/*

WORKDIR /app
EXPOSE 8080


# This stage is used to build the service project
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["ClickWrap.Server.csproj", "."]
RUN dotnet restore "./ClickWrap.Server.csproj"
COPY . .
WORKDIR "/src/."
RUN dotnet build "./ClickWrap.Server.csproj" -c $BUILD_CONFIGURATION -o /app/build

# This stage is used to publish the service project to be copied to the final stage
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
# Same version as the image label below, so the startup banner and the label cannot disagree.
ARG APP_VERSION=1.0.0
RUN dotnet publish "./ClickWrap.Server.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false /p:Version=$APP_VERSION

# This stage is used in production or when running from VS in regular mode (Default when not using the Debug configuration)
FROM base AS final

# Build with: docker build --build-arg APP_VERSION=1.2.3 -t clickwrap:1.2.3 .
ARG APP_VERSION=1.0.0
LABEL org.opencontainers.image.title="ClickWrap Server" \
      org.opencontainers.image.description="ClickWrap web app and API service" \
      org.opencontainers.image.version="${APP_VERSION}"
ENV CLICKWRAP_DATA=/data \
    CLICKWRAP_MAX_UPLOAD_MB=512 \
    CLICKWRAP_PUBLIC_BASE_URL=https://clickwrap.public.domain

WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "ClickWrap.Server.dll"]