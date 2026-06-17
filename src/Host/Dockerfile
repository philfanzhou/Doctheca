# ========== Stage 1: Build Frontend ==========
FROM node:20-alpine AS frontend-build
ARG NPM_REGISTRY=https://mirrors.huaweicloud.com/repository/npm/
ENV npm_config_registry=${NPM_REGISTRY}
ENV npm_config_replace_registry_host=always
WORKDIR /frontend
COPY src/services/ruoyu.docretrieval/frontend/package*json ./
RUN npm ci --registry=${NPM_REGISTRY}
COPY src/services/ruoyu.docretrieval/frontend/ ./
RUN npm run build

# ========== Stage 2: Build & Publish Backend ==========
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS backend-build
WORKDIR /src

COPY src/services/ruoyu.docretrieval/src/Contract/Ruoyu.Study.DocRetrieval.Contract.csproj ./src/services/ruoyu.docretrieval/src/Contract/
COPY src/services/ruoyu.docretrieval/src/Database/Ruoyu.Study.DocRetrieval.Database.csproj ./src/services/ruoyu.docretrieval/src/Database/
COPY src/services/ruoyu.docretrieval/src/Domain/Ruoyu.Study.DocRetrieval.Domain.csproj ./src/services/ruoyu.docretrieval/src/Domain/
COPY src/services/ruoyu.docretrieval/src/Service/Ruoyu.Study.DocRetrieval.Service.csproj ./src/services/ruoyu.docretrieval/src/Service/
COPY src/services/ruoyu.docretrieval/src/Host/Ruoyu.Study.DocRetrieval.Host.csproj ./src/services/ruoyu.docretrieval/src/Host/
COPY src/services/ruoyu.common/src/Ruoyu.Study.Common/Ruoyu.Study.Common.csproj ./src/services/ruoyu.common/src/Ruoyu.Study.Common/
COPY src/services/QuantumZhou.Identity/backend/Client/QuantumZhou.Identity.Client.csproj ./src/services/QuantumZhou.Identity/backend/Client/
COPY src/services/QuantumZhou.Identity/backend/Contract/QuantumZhou.Identity.Contract.csproj ./src/services/QuantumZhou.Identity/backend/Contract/
RUN dotnet restore "src/services/ruoyu.docretrieval/src/Host/Ruoyu.Study.DocRetrieval.Host.csproj"

COPY src/services/ruoyu.docretrieval/src/ ./src/services/ruoyu.docretrieval/src/
COPY src/services/ruoyu.common/src/Ruoyu.Study.Common/ ./src/services/ruoyu.common/src/Ruoyu.Study.Common/
COPY src/services/QuantumZhou.Identity/backend/Client/ ./src/services/QuantumZhou.Identity/backend/Client/
COPY src/services/QuantumZhou.Identity/backend/Contract/ ./src/services/QuantumZhou.Identity/backend/Contract/
COPY --from=frontend-build /wwwroot ./src/services/ruoyu.docretrieval/src/Host/wwwroot
RUN dotnet publish "src/services/ruoyu.docretrieval/src/Host/Ruoyu.Study.DocRetrieval.Host.csproj" -c Release -o /app/publish /p:UseAppHost=false --no-restore

# ========== Stage 3: Runtime ==========
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=backend-build /app/publish .

EXPOSE 5011 5012
ENV Endpoints__Grpc=5011
ENV Endpoints__Http=5012

ENTRYPOINT ["dotnet", "Ruoyu.Study.DocRetrieval.Host.dll"]
