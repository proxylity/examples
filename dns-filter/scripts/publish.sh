#!/bin/bash
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."
mkdir -p .build
dotnet publish src/dns-blocklist-sync/dns-blocklist-sync.csproj \
  -c Release -r linux-x64 --self-contained true -o .build/worker
tar -czf .build/worker.tar.gz -C .build/worker .
if [[ "${ENABLE_ADMIN_UI:-true}" == true ]]; then
(
  cd src/dns-admin-web
  npm ci --omit=dev
  zip -qr ../../.build/admin.zip index.js app.js rules.js policy.js services.js package.json package-lock.json node_modules
)
fi
