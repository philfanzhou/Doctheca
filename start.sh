#!/bin/bash
set -e

SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
IMAGE_TAG="20260619"
IMAGE_NAME="ruoyu.docretrieval:${IMAGE_TAG}"
CONTAINER_NAME="ruoyu-docretrieval"
NETWORK_NAME="ruoyu-net"
GRPC_PORT="5011"
HTTP_PORT="5012"

IDENTITY_GRPC_ENDPOINT="http://ruoyu-identity:5001"
IDENTITY_JWKS_ENDPOINT="http://ruoyu-identity:5002/.well-known/jwks"

OPENSEARCH_URL="http://ruoyu-opensearch:9200"
OPENSEARCH_INDEX="docretrieval-segments"

DB_HOST="ruoyu-postgres"
DB_PORT="5432"
DB_NAME="ruoyu_study_docretrieval"
DB_USER="postgres"
DB_PASS="postgres"

CONNECTION_STRING="Host=${DB_HOST};Port=${DB_PORT};Database=${DB_NAME};Username=${DB_USER};Password=${DB_PASS};"

OSS_ENDPOINT="ruoyu-seaweedfs:8333"
OSS_ACCESS_KEY="seaweedfs_admin"
OSS_SECRET_KEY="seaweedfs_admin"
OSS_BUCKET="ruoyu-study"

# LLM Segmentation - only parameters that vary per deployment
# Leave LLM_API_KEY empty to disable LLM segmentation
LLM_API_KEY=""
LLM_BASE_URL="https://api.siliconflow.cn/v1"
LLM_MODEL="Qwen/Qwen2.5-7B-Instruct"
LLM_CONTEXT_LENGTH="128K"    # Model context window: "128K", "256K", "1M" etc. Must match the actual model.
LLM_MAX_TOKENS="4K"          # Max output tokens: "4K", "8K", "128K" etc. Check model docs for actual limit.

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
  -p "${HTTP_PORT}:${HTTP_PORT}" \
  -e TZ=Asia/Shanghai \
  -e Endpoints__Grpc="${GRPC_PORT}" \
  -e Endpoints__Http="${HTTP_PORT}" \
  -e ConnectionStrings__Default="${CONNECTION_STRING}" \
  -e Oss__Endpoint="${OSS_ENDPOINT}" \
  -e Oss__AccessKey="${OSS_ACCESS_KEY}" \
  -e Oss__SecretKey="${OSS_SECRET_KEY}" \
  -e Oss__BucketName="${OSS_BUCKET}" \
  -e OpenSearch__Url="${OPENSEARCH_URL}" \
  -e OpenSearch__IndexName="${OPENSEARCH_INDEX}" \
  -e Identity__GrpcEndpoint="${IDENTITY_GRPC_ENDPOINT}" \
  -e Identity__JwksEndpoint="${IDENTITY_JWKS_ENDPOINT}" \
  -e LlmSegmentation__ApiKey="${LLM_API_KEY}" \
  -e LlmSegmentation__BaseUrl="${LLM_BASE_URL}" \
  -e LlmSegmentation__Model="${LLM_MODEL}" \
  -e LlmSegmentation__ContextLength="${LLM_CONTEXT_LENGTH:-128K}" \
  -e LlmSegmentation__MaxTokens="${LLM_MAX_TOKENS:-4096}" \
  -e Logging__LogLevel__Microsoft_EntityFrameworkCore_Database_Command="Warning" \
  "$IMAGE_NAME"

echo "${CONTAINER_NAME} started"
docker logs -f -t "$CONTAINER_NAME"
