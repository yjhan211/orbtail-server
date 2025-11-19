#!/bin/bash

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

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
ScholarCamp Development Helper

Usage: ./dev.sh [command]

Commands:
    start           Start all services with docker-compose
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

Examples:
    ./dev.sh start
    ./dev.sh logs game_server
    ./dev.sh k-logs
EOF
}

function start_services() {
    print_header "Starting Services"
    docker-compose up -d
    print_success "Services started"
    echo ""
    print_warning "Waiting for services to be ready..."
    sleep 5
    show_status
}

function stop_services() {
    print_header "Stopping Services"
    docker-compose down
    print_success "Services stopped"
}

function restart_services() {
    print_header "Restarting Services"
    docker-compose restart
    print_success "Services restarted"
}

function show_logs() {
    local service=$1
    if [ -z "$service" ]; then
        docker-compose logs -f --tail=100
    else
        docker-compose logs -f --tail=100 "$service"
    fi
}

function build_images() {
    print_header "Building Docker Images"
    docker-compose build --no-cache
    print_success "Images built"
}

function clean_all() {
    print_header "Cleaning Up"
    docker-compose down -v
    print_success "Cleanup complete"
}

function show_status() {
    print_header "Service Status"
    docker-compose ps
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
        print_error "Unhealthy or not ready"
    fi

    echo -n "Redis: "
    if docker-compose exec -T redis redis-cli ping > /dev/null 2>&1; then
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

    docker-compose exec "$service" /bin/sh
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
