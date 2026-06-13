#!/bin/bash
set -e

SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
IMAGE_TAG="20260602"
IMAGE_NAME="ruoyu.docretrieval:${IMAGE_TAG}"
CONTAINER_NAME="ruoyu-docretrieval"
NETWORK_NAME="ruoyu-net"

DOCRETRIEVAL_GRPC_PORT="10911"
DOCRETRIEVAL_HTTP_PORT="10912"

OSS_ENDPOINT="ruoyu-seaweedfs:8333"
OSS_ACCESS_KEY="seaweedfs_admin"
OSS_SECRET_KEY="seaweedfs_admin"
OSS_BUCKET="ruoyu-study"

POSTGRES_HOST="ruoyu-postgres"
POSTGRES_PORT="5432"
POSTGRES_DB="ruoyu_study_docretrieval"
POSTGRES_USER="postgres"
POSTGRES_PASSWORD="postgres"

OPENSEARCH_URL="http://ruoyu-opensearch:9200"

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
  -p "${DOCRETRIEVAL_GRPC_PORT}:5011" \
  -p "${DOCRETRIEVAL_HTTP_PORT}:5012" \
  -e TZ=Asia/Shanghai \
  -e ConnectionStrings__Default="Host=${POSTGRES_HOST};Port=${POSTGRES_PORT};Database=${POSTGRES_DB};Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD}" \
  -e Oss__Endpoint="${OSS_ENDPOINT}" \
  -e Oss__AccessKey="${OSS_ACCESS_KEY}" \
  -e Oss__SecretKey="${OSS_SECRET_KEY}" \
  -e Oss__BucketName="${OSS_BUCKET}" \
  -e OpenSearch__Url="${OPENSEARCH_URL}" \
  -e OpenSearch__IndexName="docretrieval-segments" \
  "$IMAGE_NAME"

echo "${CONTAINER_NAME} started"
echo "-> gRPC Port: ${DOCRETRIEVAL_GRPC_PORT}"
echo "-> HTTP Port: ${DOCRETRIEVAL_HTTP_PORT}"
echo "-> PostgreSQL: ${POSTGRES_HOST}:${POSTGRES_PORT}/${POSTGRES_DB}"
echo "-> OpenSearch: ${OPENSEARCH_URL}"
echo "-> OSS: ${OSS_ENDPOINT}"
echo "-> Network: ${NETWORK_NAME}"
echo "-> Image: $IMAGE_NAME"
echo "=== Real-time Logs ==="
docker logs -f -t "$CONTAINER_NAME"
