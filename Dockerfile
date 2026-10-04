# ========== Stage 1: Build Frontend ==========
FROM node:20-alpine AS frontend-build
ARG NPM_REGISTRY=https://mirrors.huaweicloud.com/repository/npm/
ENV npm_config_registry=${NPM_REGISTRY}
ENV npm_config_replace_registry_host=always
WORKDIR /frontend
COPY frontend/package*json ./
RUN npm ci --registry=${NPM_REGISTRY}
COPY frontend/ ./
RUN npm run build

# ========== Stage 2: Build & Publish Backend ==========
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend-build
WORKDIR /src

COPY src/Common/Doctheca.Common.csproj src/Common/
COPY src/Ai/Doctheca.Ai.csproj src/Ai/
COPY src/Consul/Doctheca.Consul.csproj src/Consul/
COPY src/Database/Doctheca.Database.csproj src/Database/
COPY src/Domain/Doctheca.Domain.csproj src/Domain/
COPY src/Service/Doctheca.Service.csproj src/Service/
COPY src/Host/Doctheca.Host.csproj src/Host/

RUN dotnet restore "src/Host/Doctheca.Host.csproj"

COPY src/ src/

# Inject the admin frontend build artifacts into the Host's wwwroot
COPY --from=frontend-build /wwwroot src/Host/wwwroot

RUN dotnet publish "src/Host/Doctheca.Host.csproj" -c Release -o /app/publish /p:UseAppHost=false

# ========== Stage 3: Runtime ==========
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final

# Keep dotnet as a child so fatal signals terminate it normally, and preserve its
# exit status. The default image must not require callers to add docker --init.
RUN apt-get update \
    && apt-get install -y --no-install-recommends tini \
    && rm -rf /var/lib/apt/lists/*

# No LibreOffice dependency: document parsing and Office-to-PDF conversion are
# delegated to the external StructaDoc service (ADR-0009).
# See docs/modules/DocumentParse/03-DESIGN.md for details.

WORKDIR /app
COPY --from=backend-build /app/publish .

EXPOSE 5012
ENV Endpoints__Http=5012

ENTRYPOINT ["/usr/bin/tini", "--", "dotnet", "Doctheca.Host.dll"]
