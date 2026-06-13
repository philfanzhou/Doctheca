#!/bin/bash
set -e

SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
IMAGE_TAG="20260613"
IMAGE_NAME="ruoyu.docretrieval:${IMAGE_TAG}"
CONTAINER_NAME="ruoyu-docretrieval"
NETWORK_NAME="ruoyu-net"

# Prerequisites: start these services first
#   .script/env-script/01-start-postgres.sh
#   .script/env-script/03-start-seaweedfs.sh
#   .script/env-script/04-start-opensearch.sh

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
  -e TZ=Asia/Shanghai \
  -e Endpoints__Grpc="5011" \
  -e Endpoints__Http="5012" \
  -e ConnectionStrings__Default="${CONNECTION_STRING}" \
  -e Oss__Endpoint="${OSS_ENDPOINT}" \
  -e Oss__AccessKey="${OSS_ACCESS_KEY}" \
  -e Oss__SecretKey="${OSS_SECRET_KEY}" \
  -e Oss__BucketName="${OSS_BUCKET}" \
  -e OpenSearch__Url="${OPENSEARCH_URL}" \
  -e OpenSearch__IndexName="docretrieval-segments" \
  "$IMAGE_NAME"

echo "${CONTAINER_NAME} started"
echo "-> gRPC: 5011 (internal)"
echo "-> HTTP: 5012 (internal)"
echo "-> DB: ${DB_HOST}:${DB_PORT}/${DB_NAME}"
echo "-> OpenSearch: ${OPENSEARCH_URL}"
echo "-> OSS: ${OSS_ENDPOINT}"
echo "-> Network: ${NETWORK_NAME}"
echo "-> Image: ${IMAGE_NAME}"
echo "=== Real-time Logs ==="
docker logs -f -t "$CONTAINER_NAME"
