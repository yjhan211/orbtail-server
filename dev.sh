#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"
show_help() {
    cat <<'HELP'
OrbTail server development
Usage: ./dev.sh start|stop|restart|logs [service]|build|status|shell <service>|clean|config
Copy .env.local.example to .env.local to set the advertised game server IP.
SCALE=1 ./dev.sh start          Start two game servers and two user servers
Use the same SCALE value for subsequent commands, including stop.
clean removes the selected Compose project's data volumes.
HELP
}
if [[ "${1:-help}" == "help" || "${1:-}" == "--help" || "${1:-}" == "-h" ]]; then
    show_help
    exit 0
fi
if ! docker compose version >/dev/null 2>&1; then
    echo "Docker Compose v2 is required." >&2
    exit 1
fi
compose=(docker compose)
if [[ -f .env.local ]]; then
    compose+=(--env-file .env.local)
fi
compose+=(-f docker-compose.local.yml)
if [[ "${SCALE:-0}" == "1" ]]; then
    compose+=(-f docker-compose.scale.yml)
fi
case "$1" in
    start) "${compose[@]}" up -d --build ;;
    stop) "${compose[@]}" down ;;
    restart) "${compose[@]}" restart ;;
    logs) shift; "${compose[@]}" logs -f --tail=100 "$@" ;;
    build) "${compose[@]}" build ;;
    status) "${compose[@]}" ps ;;
    shell) : "${2:?Specify a service name}"; "${compose[@]}" exec "$2" /bin/sh ;;
    clean) "${compose[@]}" down -v ;;
    config) "${compose[@]}" config --quiet ;;
    *) show_help; exit 1 ;;
esac
