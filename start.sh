#!/bin/bash
set -e

SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
IMAGE_NAME="ruoyu.doclibrary:20260619"
CONTAINER_NAME="ruoyu-doclibrary"
NETWORK_NAME="ruoyu-net"
HTTP_PORT="5012"

CONSUL_HTTP_ADDR="${CONSUL_HTTP_ADDR:-host.docker.internal:8500}"
CONSUL_TOKEN="${CONSUL_TOKEN:-}"

IDENTITY_AUTHORITY="${IDENTITY_AUTHORITY:-http://ruoyu-identity:5002}"
IDENTITY_APP_ID="${IDENTITY_APP_ID:-}"
IDENTITY_APP_SECRET="${IDENTITY_APP_SECRET:-}"
DOCLIBRARY_COOKIE_SECURE="${DOCLIBRARY_COOKIE_SECURE:-false}"
DOCLIBRARY_QUESTIONBANK_KEY="${DOCLIBRARY_QUESTIONBANK_KEY:-}"

if { [ -n "$IDENTITY_APP_ID" ] && [ -z "$IDENTITY_APP_SECRET" ]; } ||
   { [ -z "$IDENTITY_APP_ID" ] && [ -n "$IDENTITY_APP_SECRET" ]; }; then
    echo "Identity AppId and AppSecret must be configured together." >&2
    exit 1
fi

if [ -z "$DOCLIBRARY_QUESTIONBANK_KEY" ]; then
    echo "DOCLIBRARY_QUESTIONBANK_KEY is required." >&2
    exit 1
fi

OPENSEARCH_INDEX="doclibrary-segments"

DB_NAME="ruoyu_study_doclibrary"

LLM_API_KEY=""
LLM_BASE_URL="https://api.siliconflow.cn/v1"
LLM_MODEL="Qwen/Qwen2.5-7B-Instruct"
LLM_CONTEXT_LENGTH="128K"

MINERU_API_TOKEN=""

docker network inspect "$NETWORK_NAME" >/dev/null 2>&1 || docker network create "$NETWORK_NAME"
if [ -n "$(docker ps -q --filter "name=^/${CONTAINER_NAME}$")" ]; then
    echo "Container is already running, stopping it..."
    docker stop "$CONTAINER_NAME"
fi
if [ -n "$(docker ps -aq --filter "name=^/${CONTAINER_NAME}$")" ]; then
    echo "Removing old container..."
    docker rm "$CONTAINER_NAME"
fi

docker run -d \
  --name "$CONTAINER_NAME" \
  --restart unless-stopped \
  --network "$NETWORK_NAME" \
  --add-host=host.docker.internal:host-gateway \
  -p "${HTTP_PORT}:${HTTP_PORT}" \
  -e TZ=Asia/Shanghai \
  -e CONSUL_HTTP_ADDR="${CONSUL_HTTP_ADDR}" \
  -e CONSUL_TOKEN="${CONSUL_TOKEN}" \
  -e Endpoints__Http="${HTTP_PORT}" \
  -e Database__Name="${DB_NAME}" \
  -e IdentityService__Authority="${IDENTITY_AUTHORITY}" \
  -e IdentityService__AppId="${IDENTITY_APP_ID}" \
  -e IdentityService__AppSecret="${IDENTITY_APP_SECRET}" \
  -e Authentication__CookieSecure="${DOCLIBRARY_COOKIE_SECURE}" \
  -e InternalAuth__QuestionBankKey="${DOCLIBRARY_QUESTIONBANK_KEY}" \
  -e OpenSearch__IndexName="${OPENSEARCH_INDEX}" \
  -e LlmDocumentAnalysis__ApiKey="${LLM_API_KEY}" \
  -e LlmDocumentAnalysis__BaseUrl="${LLM_BASE_URL}" \
  -e LlmDocumentAnalysis__Model="${LLM_MODEL}" \
  -e LlmDocumentAnalysis__ContextLength="${LLM_CONTEXT_LENGTH:-128K}" \
  -e MinerU__ApiToken="${MINERU_API_TOKEN}" \
  -e Logging__LogLevel__Microsoft_EntityFrameworkCore_Database_Command="Warning" \
  "$IMAGE_NAME"

echo "${CONTAINER_NAME} started"
docker logs -f -t "$CONTAINER_NAME"
