#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
deployment_dir="$repository_root/deployment"
if [[ -n "$(git -C "$repository_root" status --porcelain)" ]]; then
  echo "Refusing update: the server repository has local changes." >&2
  exit 1
fi
git -C "$repository_root" pull --ff-only origin main
cd "$deployment_dir"
docker compose up -d --build
docker compose ps
http_port="$(sed -n 's/^VAULTIX_HTTP_PORT=//p' .env | tail -n 1)"
http_port="${http_port:-7443}"
curl --fail --silent "http://127.0.0.1:${http_port}/api/v1/health"
echo
