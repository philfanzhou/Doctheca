#!/bin/bash
set -e

# Set IMAGE_REPO=ghcr.io/philfanzhou/doctheca to run a published release instead of a local build.
IMAGE_REPO="${IMAGE_REPO:-doctheca}"
IMAGE_TAG="${IMAGE_TAG:-latest}"
IMAGE_NAME="${IMAGE_REPO}:${IMAGE_TAG}"
CONTAINER_NAME="doctheca"
HTTP_PORT="5012"

CONSUL_HTTP_ADDR="${CONSUL_HTTP_ADDR:-192.168.100.10:8500}"
CONSUL_TOKEN="${CONSUL_TOKEN:-}"

IDENTITY_APP_ID="${IDENTITY_APP_ID:-}"
IDENTITY_APP_SECRET="${IDENTITY_APP_SECRET:-}"

OPENSEARCH_INDEX="doctheca-segments"

DB_NAME="doctheca"

# Optional: allow startup to create the missing target database (see docs/development/Deployment.md).
# Empty keeps the default refusal (database_target_preparation.creation_not_allowed); set to true/false to override.
DATABASE_ALLOW_CREATE="${DATABASE_ALLOW_CREATE:-}"

LLM_API_KEY="${LLM_API_KEY:-}"
LLM_BASE_URL="${LLM_BASE_URL:-https://api.siliconflow.cn/v1}"
LLM_MODEL="${LLM_MODEL:-Qwen/Qwen2.5-7B-Instruct}"
LLM_CONTEXT_LENGTH="${LLM_CONTEXT_LENGTH:-128K}"

STRUCTADOC_BASE_URL="${STRUCTADOC_BASE_URL:-http://structadoc:8080}"
STRUCTADOC_API_KEY="${STRUCTADOC_API_KEY:-}"

if [ -n "$(docker ps -q --filter "name=^/${CONTAINER_NAME}$")" ]; then
    echo "Container is already running, stopping it..."
    docker stop "$CONTAINER_NAME"
fi
if [ -n "$(docker ps -aq --filter "name=^/${CONTAINER_NAME}$")" ]; then
    echo "Removing old container..."
    docker rm "$CONTAINER_NAME"
fi

# The image includes Tini; startup failures propagate a non-zero container exit.
# The restart policy still retries failures; inspect restart count and logs when diagnosing.
docker run -d \
  --name "$CONTAINER_NAME" \
  --restart unless-stopped \
  -p "${HTTP_PORT}:${HTTP_PORT}" \
  -e TZ=Asia/Shanghai \
  -e CONSUL_HTTP_ADDR="${CONSUL_HTTP_ADDR}" \
  -e CONSUL_TOKEN="${CONSUL_TOKEN}" \
  -e Endpoints__Http="${HTTP_PORT}" \
  -e Database__Name="${DB_NAME}" \
  ${DATABASE_ALLOW_CREATE:+-e Database__AllowCreate="${DATABASE_ALLOW_CREATE}"} \
  -e IdentityService__AppId="${IDENTITY_APP_ID}" \
  -e IdentityService__AppSecret="${IDENTITY_APP_SECRET}" \
  -e OpenSearch__IndexName="${OPENSEARCH_INDEX}" \
  -e LlmDocumentAnalysis__ApiKey="${LLM_API_KEY}" \
  -e LlmDocumentAnalysis__BaseUrl="${LLM_BASE_URL}" \
  -e LlmDocumentAnalysis__Model="${LLM_MODEL}" \
  -e LlmDocumentAnalysis__ContextLength="${LLM_CONTEXT_LENGTH:-128K}" \
  -e StructaDoc__BaseUrl="${STRUCTADOC_BASE_URL}" \
  -e StructaDoc__ApiKey="${STRUCTADOC_API_KEY}" \
  -e Logging__LogLevel__Microsoft_EntityFrameworkCore_Database_Command="Warning" \
  "$IMAGE_NAME"
