// Ruoyu.Study - DocLibrary Service Pipeline
//
// Per-service pipeline for ruoyu.doclibrary:
//   1. Preflight — verify docker + dotnet + repo access
//   2. Build    — reuse script/build-script/02-doclibrary.build.sh (docker build)
//   3. UT       — run unit tests directly on host via dotnet SDK 8.0
//   4. Deploy   — restart the ruoyu-doclibrary container via start.sh
//   5. Smoke    — health check through the published host port
//
// The Jenkins agent must run on the DocLibrary deployment host. Set CONSUL_HTTP_ADDR
// to the reachable Consul LAN endpoint and provide CONSUL_TOKEN through credentials.

pipeline {
    agent any

    options {
        timestamps()
        timeout(time: 30, unit: 'MINUTES')
        buildDiscarder(logRotator(numToKeepStr: '20'))
        disableConcurrentBuilds()
    }

    environment {
        REPO_DIR         = "${env.REPO_DIR ?: '/srv/repo'}"
        SERVICE_DIR      = "${env.REPO_DIR}/src/services/ruoyu.doclibrary"
        BUILD_SCRIPT     = "${env.REPO_DIR}/script/build-script/02-doclibrary.build.sh"
        TEST_PROJ        = "${env.SERVICE_DIR}/src/Tests/Ruoyu.Study.DocLibrary.Tests/Ruoyu.Study.DocLibrary.Tests.csproj"
        START_SCRIPT     = "${env.SERVICE_DIR}/start.sh"
        REPORT_DIR       = "${env.WORKSPACE}/reports"
        DOCLIBRARY_SMOKE_URL = "${env.DOCLIBRARY_SMOKE_URL ?: 'http://127.0.0.1:5012/health'}"
        NUGET_SOURCE     = 'https://repo.huaweicloud.com/repository/nuget/v3/index.json'
        CONSUL_TOKEN     = credentials('consul-acl-token')
    }

    triggers { pollSCM('H/5 * * * *') }

    stages {
        stage('Preflight') {
            steps {
                sh '''
                    set -e
                    echo "=== Jenkins user ==="
                    id
                    echo ""
                    echo "=== Docker access ==="
                    docker info --format 'Server Version: {{.ServerVersion}}'
                    echo ""
                    echo "=== dotnet SDK ==="
                    dotnet --version
                    echo ""
                    echo "=== Repo symlink ==="
                    ls -la "$REPO_DIR" | head -10
                    echo ""
                    echo "=== DocLibrary source tree ==="
                    ls -la "$SERVICE_DIR"
                    echo ""
                    echo "=== Build script ==="
                    ls -la "$BUILD_SCRIPT"
                    echo ""
                    echo "=== Test project ==="
                    ls -la "$TEST_PROJ"
                '''
            }
        }

        stage('Build Image') {
            steps {
                sh '''
                    set -e
                    cd "$REPO_DIR"
                    bash "$BUILD_SCRIPT"
                    docker images ruoyu.doclibrary --format '{{.Repository}}:{{.Tag}} {{.CreatedSince}} {{.Size}}'
                '''
            }
        }

        stage('Unit Test') {
            steps {
                sh '''
                    set -e
                    mkdir -p "$REPORT_DIR"
                    cd "$SERVICE_DIR"
                    # Clean ALL stale obj/bin under ruoyu.common and this service —
                    # a previous Docker build leaves incomplete project.assets.json
                    # (empty projectReferences -> CS0234) and missing ref DLLs
                    # (obj/Release/net8.0/ref/ empty -> CS0006) for every transitively
                    # restored project, not just Tests.
                    COMMON_DIR="$REPO_DIR/src/ruoyu.common"
                    find "$COMMON_DIR" -type d \\( -name obj -o -name bin \\) -prune -exec rm -rf {} + 2>/dev/null || true
                    find "$SERVICE_DIR" -type d \\( -name obj -o -name bin \\) -prune -exec rm -rf {} + 2>/dev/null || true
                    dotnet restore "$TEST_PROJ" --source "$NUGET_SOURCE"
                    dotnet test "$TEST_PROJ" \
                        --configuration Release \
                        --logger 'trx;logfilename=doclibrary-ut.trx' \
                        --results-directory "$REPORT_DIR" \
                        --no-restore
                    echo 'UT completed'
                '''
            }
        }

        stage('Deploy') {
            steps {
                withCredentials([string(credentialsId: 'consul-acl-token', variable: 'CONSUL_TOKEN_BIND')]) {
                    sh '''
                        set -e
                        if [ -z "$CONSUL_TOKEN" ] && [ -n "$CONSUL_TOKEN_BIND" ]; then
                            export CONSUL_TOKEN="$CONSUL_TOKEN_BIND"
                        fi
                        cd "$SERVICE_DIR"
                        bash "$START_SCRIPT"
                        docker ps --filter 'name=ruoyu-doclibrary' --format '{{.Names}} {{.Status}}'
                    '''
                }
            }
        }

        stage('Smoke Test') {
            steps {
                sh '''
                    set -e
                    for i in $(seq 1 30); do
                        if curl -fsS --max-time 3 "$DOCLIBRARY_SMOKE_URL" >/dev/null 2>&1; then
                            echo "DocLibrary ready after ${i}s"
                            exit 0
                        fi
                        sleep 1
                    done
                    echo "DocLibrary smoke test failed: $DOCLIBRARY_SMOKE_URL"
                    exit 1
                '''
            }
        }
    }

    post {
        always {
            echo "=== Collecting artifacts ==="
            script {
                sh '''
                    mkdir -p "$REPORT_DIR"
                    ls -la "$REPORT_DIR/" || true
                '''
            }
            archiveArtifacts artifacts: 'reports/**', allowEmptyArchive: true
        }
        success {
            echo 'DocLibrary pipeline PASSED: build + UT + deploy + smoke'
        }
        failure {
            echo 'DocLibrary pipeline FAILED — check logs above'
        }
    }
}
