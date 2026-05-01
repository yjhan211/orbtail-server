#!/bin/bash

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

# Docker Compose command (supports both v1 and v2)
if command -v docker compose &> /dev/null; then
    DOCKER_COMPOSE="docker compose"
elif command -v docker-compose &> /dev/null; then
    DOCKER_COMPOSE="docker-compose"
else
    echo "Error: Docker Compose not found. Please install Docker Compose."
    exit 1
fi

# Use local compose file by default for hot reload (개발자 PC용)
COMPOSE_FILE="${COMPOSE_FILE:-docker-compose.local.yml}"
DOCKER_COMPOSE="$DOCKER_COMPOSE -f $COMPOSE_FILE"

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

function print_header() {
    echo -e "${BLUE}========================================${NC}"
    echo -e "${BLUE}$1${NC}"
    echo -e "${BLUE}========================================${NC}"
}

function print_success() {
    echo -e "${GREEN}✓ $1${NC}"
}

function print_error() {
    echo -e "${RED}✗ $1${NC}"
}

function print_warning() {
    echo -e "${YELLOW}⚠ $1${NC}"
}

function show_help() {
    cat << EOF
Manitto Development Helper

Usage: ./dev.sh [command]
       COMPOSE_FILE=docker-compose.prod.yml ./dev.sh [command]  (for prod-like mode)

Commands:
    start           Start all services (uses local mode with hot reload by default)
    stop            Stop all services
    restart         Restart all services
    logs [service]  Show logs (optionally for specific service)
    build           Build all Docker images
    clean           Stop and remove all containers and volumes
    status          Show status of all services
    shell [service] Open a shell in a service container
    deploy          Deploy to Kubernetes cluster
    k-logs          Show Kubernetes pod logs (interactive)
    help            Show this help message

Local Mode (default — 개발자 PC용):
    - Uses docker-compose.local.yml
    - Hot reload enabled with dotnet watch
    - Code changes are automatically detected and server restarts
    - Just edit your code and save!

Prod-like Mode (외부 dev/prod 환경 검증용):
    COMPOSE_FILE=docker-compose.prod.yml ./dev.sh start
    - Uses GHCR Docker images (optimized release build)
    - Requires .env.dev or .env.prod for environment variables

Examples:
    ./dev.sh start                    # Start with hot reload (local mode)
    ./dev.sh logs game_server         # View game server logs
    ./dev.sh k-logs                   # Interactive K8s log viewer
    COMPOSE_FILE=docker-compose.prod.yml ./dev.sh start  # Prod-like mode
EOF
}

function stop_conflicting_mode() {
    # local 모드 시작 시 infra-only 중지, infra-only 시작 시 local 중지
    if [[ "$COMPOSE_FILE" == *"local.yml"* ]]; then
        # infra-only가 실행 중이면 중지
        if docker compose -f docker-compose.infra.yml ps --status running 2>/dev/null | grep -q manitto; then
            print_warning "infra-only 모드가 실행 중입니다. 중지합니다..."
            docker compose -f docker-compose.infra.yml down
        fi
    else
        # local 모드가 실행 중이면 중지
        if docker compose -f docker-compose.local.yml ps --status running 2>/dev/null | grep -q manitto; then
            print_warning "local 모드가 실행 중입니다. 중지합니다..."
            docker compose -f docker-compose.local.yml down
        fi
    fi
}

function start_services() {
    print_header "Starting Services"

    stop_conflicting_mode

    if [[ "$COMPOSE_FILE" == *"local.yml"* ]]; then
        print_warning "Starting in LOCAL mode with hot reload..."
        echo "Code changes will be automatically detected!"
        $DOCKER_COMPOSE up -d --build
    else
        print_warning "Starting in PROD-LIKE mode..."
        $DOCKER_COMPOSE up -d
    fi

    print_success "Services started"
    echo ""
    print_warning "Waiting for services to be ready..."
    sleep 5
    show_status
}

function stop_services() {
    print_header "Stopping Services"
    $DOCKER_COMPOSE down
    print_success "Services stopped"
}

function restart_services() {
    print_header "Restarting Services"
    $DOCKER_COMPOSE restart
    print_success "Services restarted"
}

function show_logs() {
    local service=$1
    if [ -z "$service" ]; then
        $DOCKER_COMPOSE logs -f --tail=100
    else
        $DOCKER_COMPOSE logs -f --tail=100 "$service"
    fi
}

function build_images() {
    print_header "Building Docker Images"
    $DOCKER_COMPOSE build --no-cache
    print_success "Images built"
}

function clean_all() {
    print_header "Cleaning Up"
    $DOCKER_COMPOSE down -v
    print_success "Cleanup complete"
}

function show_status() {
    print_header "Service Status"
    $DOCKER_COMPOSE ps
    echo ""

    # Check health endpoints
    print_header "Health Check Status"

    echo -n "Game Server: "
    if curl -s http://localhost:8080/health/ready > /dev/null 2>&1; then
        print_success "Healthy"
    else
        print_error "Unhealthy or not ready"
    fi

    echo -n "User Server: "
    if curl -s http://localhost:8081/health/ready > /dev/null 2>&1; then
        print_success "Healthy"
    else
        print_error "Unhealthy or not ready"
    fi

    echo -n "Redis: "
    if $DOCKER_COMPOSE exec -T redis redis-cli ping > /dev/null 2>&1; then
        print_success "Healthy"
    else
        print_error "Unhealthy"
    fi

    echo -n "NATS: "
    if curl -s http://localhost:8222/healthz > /dev/null 2>&1; then
        print_success "Healthy"
    else
        print_error "Unhealthy"
    fi
}

function open_shell() {
    local service=$1
    if [ -z "$service" ]; then
        print_error "Please specify a service name"
        echo "Available services: game_server, user_server, redis, nats"
        exit 1
    fi

    $DOCKER_COMPOSE exec "$service" /bin/sh
}

function deploy_k8s() {
    print_header "Deploying to Kubernetes"
    ./deploy.sh
}

function k8s_logs() {
    print_header "Kubernetes Pod Logs"

    # Get namespace
    NAMESPACE=${1:-app}

    # List all pods
    echo "Available pods in namespace '$NAMESPACE':"
    kubectl get pods -n "$NAMESPACE" --no-headers | nl
    echo ""

    # Interactive selection
    read -p "Select pod number (or 'q' to quit): " pod_num

    if [ "$pod_num" = "q" ]; then
        exit 0
    fi

    POD_NAME=$(kubectl get pods -n "$NAMESPACE" --no-headers | sed -n "${pod_num}p" | awk '{print $1}')

    if [ -z "$POD_NAME" ]; then
        print_error "Invalid selection"
        exit 1
    fi

    print_header "Showing logs for: $POD_NAME"

    # Check if pod has multiple containers
    CONTAINER_COUNT=$(kubectl get pod "$POD_NAME" -n "$NAMESPACE" -o jsonpath='{.spec.containers[*].name}' | wc -w)

    if [ "$CONTAINER_COUNT" -gt 1 ]; then
        echo "Multiple containers found:"
        kubectl get pod "$POD_NAME" -n "$NAMESPACE" -o jsonpath='{.spec.containers[*].name}' | tr ' ' '\n' | nl
        echo ""
        read -p "Select container number: " container_num
        CONTAINER_NAME=$(kubectl get pod "$POD_NAME" -n "$NAMESPACE" -o jsonpath='{.spec.containers[*].name}' | tr ' ' '\n' | sed -n "${container_num}p")

        if [ -z "$CONTAINER_NAME" ]; then
            print_error "Invalid selection"
            exit 1
        fi

        kubectl logs -f "$POD_NAME" -n "$NAMESPACE" -c "$CONTAINER_NAME" --tail=100
    else
        kubectl logs -f "$POD_NAME" -n "$NAMESPACE" --tail=100
    fi
}

# Main command handler
case "${1:-help}" in
    start)
        start_services
        ;;
    stop)
        stop_services
        ;;
    restart)
        restart_services
        ;;
    logs)
        show_logs "$2"
        ;;
    build)
        build_images
        ;;
    clean)
        clean_all
        ;;
    status)
        show_status
        ;;
    shell)
        open_shell "$2"
        ;;
    deploy)
        deploy_k8s
        ;;
    k-logs)
        k8s_logs "$2"
        ;;
    help|--help|-h)
        show_help
        ;;
    *)
        print_error "Unknown command: $1"
        echo ""
        show_help
        exit 1
        ;;
esac
