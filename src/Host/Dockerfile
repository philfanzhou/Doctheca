# ========== Stage 1: Build Frontend ==========
FROM node:20-alpine AS frontend-build
ARG NPM_REGISTRY=https://mirrors.huaweicloud.com/repository/npm/
ENV npm_config_registry=${NPM_REGISTRY}
ENV npm_config_replace_registry_host=always
WORKDIR /frontend
COPY src/services/ruoyu.doclibrary/frontend/package*json ./
RUN npm ci --registry=${NPM_REGISTRY}
COPY src/services/ruoyu.doclibrary/frontend/ ./
RUN npm run build

# ========== Stage 2: Build & Publish Backend ==========
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS backend-build
WORKDIR /src

COPY src/services/ruoyu.doclibrary/src/Database/Ruoyu.Study.DocLibrary.Database.csproj ./src/services/ruoyu.doclibrary/src/Database/
COPY src/services/ruoyu.doclibrary/src/Domain/Ruoyu.Study.DocLibrary.Domain.csproj ./src/services/ruoyu.doclibrary/src/Domain/
COPY src/services/ruoyu.doclibrary/src/Service/Ruoyu.Study.DocLibrary.Service.csproj ./src/services/ruoyu.doclibrary/src/Service/
COPY src/services/ruoyu.doclibrary/src/Host/Ruoyu.Study.DocLibrary.Host.csproj ./src/services/ruoyu.doclibrary/src/Host/
COPY src/services/ruoyu.common/src/Ruoyu.Study.Common/Ruoyu.Study.Common.csproj ./src/services/ruoyu.common/src/Ruoyu.Study.Common/
RUN dotnet restore "src/services/ruoyu.doclibrary/src/Host/Ruoyu.Study.DocLibrary.Host.csproj"

COPY src/services/ruoyu.doclibrary/src/ ./src/services/ruoyu.doclibrary/src/
COPY src/services/ruoyu.common/src/Ruoyu.Study.Common/ ./src/services/ruoyu.common/src/Ruoyu.Study.Common/
COPY --from=frontend-build /wwwroot ./src/services/ruoyu.doclibrary/src/Host/wwwroot
RUN dotnet publish "src/services/ruoyu.doclibrary/src/Host/Ruoyu.Study.DocLibrary.Host.csproj" -c Release -o /app/publish /p:UseAppHost=false --no-restore

# ========== Stage 3: Runtime ==========
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final

# Configure apt mirror for Chinese network and install LibreOffice
RUN sed -i 's|deb.debian.org|mirrors.aliyun.com|g' /etc/apt/sources.list.d/debian.sources 2>/dev/null || \
    sed -i 's|deb.debian.org|mirrors.aliyun.com|g' /etc/apt/sources.list 2>/dev/null || true && \
    apt-get update && \
    apt-get install -y --no-install-recommends libreoffice-writer libreoffice-impress && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=backend-build /app/publish .

EXPOSE 5012
ENV Endpoints__Http=5012

ENTRYPOINT ["dotnet", "Ruoyu.Study.DocLibrary.Host.dll"]
