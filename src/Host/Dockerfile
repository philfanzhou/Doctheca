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
COPY src/ruoyu.common/backend/Ruoyu.Study.Common/Ruoyu.Study.Common.csproj ./src/ruoyu.common/backend/Ruoyu.Study.Common/
COPY src/ruoyu.common/backend/Ruoyu.Study.Ai.Shared/Ruoyu.Study.Ai.Shared.csproj ./src/ruoyu.common/backend/Ruoyu.Study.Ai.Shared/
COPY src/ruoyu.common/backend/Ruoyu.Study.Consul.Shared/Ruoyu.Study.Consul.Shared.csproj ./src/ruoyu.common/backend/Ruoyu.Study.Consul.Shared/
RUN dotnet restore "src/services/ruoyu.doclibrary/src/Host/Ruoyu.Study.DocLibrary.Host.csproj"

COPY src/services/ruoyu.doclibrary/src/ ./src/services/ruoyu.doclibrary/src/
COPY src/ruoyu.common/backend/Ruoyu.Study.Common/ ./src/ruoyu.common/backend/Ruoyu.Study.Common/
COPY src/ruoyu.common/backend/Ruoyu.Study.Ai.Shared/ ./src/ruoyu.common/backend/Ruoyu.Study.Ai.Shared/
COPY src/ruoyu.common/backend/Ruoyu.Study.Consul.Shared/ ./src/ruoyu.common/backend/Ruoyu.Study.Consul.Shared/
COPY --from=frontend-build /wwwroot ./src/services/ruoyu.doclibrary/src/Host/wwwroot
RUN dotnet publish "src/services/ruoyu.doclibrary/src/Host/Ruoyu.Study.DocLibrary.Host.csproj" -c Release -o /app/publish /p:UseAppHost=false --no-restore

# ========== Stage 3: Runtime ==========
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final

# No LibreOffice dependency: document parsing and Office-to-PDF conversion are
# delegated to the external StructaDoc service (ADR-0009).
# See docs/modules/DocumentParse/03-DESIGN.md for details.

WORKDIR /app
COPY --from=backend-build /app/publish .

EXPOSE 5012
ENV Endpoints__Http=5012

ENTRYPOINT ["dotnet", "Ruoyu.Study.DocLibrary.Host.dll"]
