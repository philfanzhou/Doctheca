#!/bin/bash
set -e

IMAGE_NAME="ruoyu.doclibrary:20260619"
CONTAINER_NAME="ruoyu-doclibrary"
HTTP_PORT="5012"

CONSUL_HTTP_ADDR="${CONSUL_HTTP_ADDR:-192.168.100.10:8500}"
CONSUL_TOKEN="${CONSUL_TOKEN:-}"

DOCLIBRARY_COOKIE_SECURE="${DOCLIBRARY_COOKIE_SECURE:-false}"

OPENSEARCH_INDEX="doclibrary-segments"

DB_NAME="ruoyu_study_doclibrary"

LLM_API_KEY=""
LLM_BASE_URL="https://api.siliconflow.cn/v1"
LLM_MODEL="Qwen/Qwen2.5-7B-Instruct"
LLM_CONTEXT_LENGTH="128K"

MINERU_API_TOKEN=""

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
  -p "${HTTP_PORT}:${HTTP_PORT}" \
  -e TZ=Asia/Shanghai \
  -e CONSUL_HTTP_ADDR="${CONSUL_HTTP_ADDR}" \
  -e CONSUL_TOKEN="${CONSUL_TOKEN}" \
  -e Endpoints__Http="${HTTP_PORT}" \
  -e Database__Name="${DB_NAME}" \
  -e Authentication__CookieSecure="${DOCLIBRARY_COOKIE_SECURE}" \
  -e OpenSearch__IndexName="${OPENSEARCH_INDEX}" \
  -e LlmDocumentAnalysis__ApiKey="${LLM_API_KEY}" \
  -e LlmDocumentAnalysis__BaseUrl="${LLM_BASE_URL}" \
  -e LlmDocumentAnalysis__Model="${LLM_MODEL}" \
  -e LlmDocumentAnalysis__ContextLength="${LLM_CONTEXT_LENGTH:-128K}" \
  -e MinerU__ApiToken="${MINERU_API_TOKEN}" \
  -e Logging__LogLevel__Microsoft_EntityFrameworkCore_Database_Command="Warning" \
  "$IMAGE_NAME"
