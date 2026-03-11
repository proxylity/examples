#!/usr/bin/env bash
# scripts/1-deploy-supabase.sh
#
# Deploys the Supabase side of the supabase-udp example:
#   1. Runs the schema migration (creates the udp_messages table)
#   2. Deploys the udp-receiver Edge Function
#   3. Generates a random HS256 signing secret and stores it in Supabase secrets
#
# Prerequisites:
#   - Node.js installed
#   - npm install  (run once from the supabase-udp root — installs supabase CLI locally)
#   - Logged in:   npx supabase login
#   - Linked:      npx supabase link --project-ref <your-project-id>
#
# After this script completes, note the two exported values:
#   SUPABASE_EDGE_FUNCTION_URL  — needed by scripts/2-deploy-aws.sh
#   JWT_SECRET                  — needed by scripts/2-deploy-aws.sh (stored in SSM)

set -euo pipefail
cd "$(dirname "$0")/.."

# Use npx to run the locally installed supabase CLI (avoids the need for a global install).
SUPABASE="npx --no-install supabase"

echo "==> Checking Supabase CLI (npx)..."
if ! npx --no-install supabase --version &>/dev/null; then
  echo "ERROR: supabase CLI not found in local node_modules."
  echo "Run: npm install  (from the supabase-udp directory)"
  exit 1
fi

echo "==> Pushing schema migration..."
$SUPABASE db push

echo "==> Deploying udp-receiver Edge Function..."
$SUPABASE functions deploy udp-receiver

echo "==> Generating JWT signing secret..."
JWT_SECRET=$(openssl rand -base64 32)

echo "==> Storing JWT_SECRET in Supabase project secrets..."
$SUPABASE secrets set JWT_SECRET="$JWT_SECRET"

echo "==> Storing SERVICE_ROLE_KEY in Supabase project secrets..."
echo ""
echo "  You must manually set SERVICE_ROLE_KEY."
echo "  Find your service_role key in the Supabase dashboard:"
echo "  Project Settings → API → Project API keys → service_role"
echo ""
read -rsp "  Paste your service_role key and press Enter: " SERVICE_ROLE_KEY
echo ""
$SUPABASE secrets set SERVICE_ROLE_KEY="$SERVICE_ROLE_KEY"

# Derive the Edge Function URL from the linked project
PROJECT_REF=$($SUPABASE status --output json 2>/dev/null | python3 -c "import sys,json; print(json.load(sys.stdin).get('project_ref',''))" 2>/dev/null || true)
if [[ -z "$PROJECT_REF" ]]; then
  echo "  Could not determine project ref automatically."
  read -rp "  Enter your Supabase project ref (e.g. xxxx from https://xxxx.supabase.co): " PROJECT_REF
fi

SUPABASE_EDGE_FUNCTION_URL="https://${PROJECT_REF}.supabase.co/functions/v1/udp-receiver"

echo ""
echo "======================================================================"
echo "  Supabase deployment complete."
echo ""
echo "  Set these environment variables before running 2-deploy-aws.sh:"
echo ""
echo "  export JWT_SECRET='${JWT_SECRET}'"
echo "  export SUPABASE_EDGE_FUNCTION_URL='${SUPABASE_EDGE_FUNCTION_URL}'"
echo "======================================================================"
