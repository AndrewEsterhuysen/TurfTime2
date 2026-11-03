#!/usr/bin/env bash
# Deploy the TurfTimer MCP server to Cloud Run. NOT RUN BY AUTOMATION: needs Andrew's approval.
# Usage:  CONFIRM=deploy ./deploy/cloudrun.sh            (from mcp-server/)
# Needs:  gcloud logged in as a project owner; FIREBASE_WEB_API_KEY exported (the public Web API key
#         from the Firebase console, same value the app ships with).
set -euo pipefail
[[ "${CONFIRM:-}" == "deploy" ]] || { echo "Refusing: set CONFIRM=deploy after approval."; exit 1; }
: "${FIREBASE_WEB_API_KEY:?export FIREBASE_WEB_API_KEY}"
PROJECT="${PROJECT:-turf-timer}"
REGION="${REGION:-australia-southeast1}"
SERVICE="${SERVICE:-turftimer-mcp}"
SA="${SERVICE}@${PROJECT}.iam.gserviceaccount.com"
SECRET="mcp-jwt-secret"

gcloud config set project "$PROJECT" >/dev/null
gcloud services enable run.googleapis.com secretmanager.googleapis.com iamcredentials.googleapis.com cloudbuild.googleapis.com artifactregistry.googleapis.com

# 1) Runtime service account (least privilege)
gcloud iam service-accounts describe "$SA" >/dev/null 2>&1 || gcloud iam service-accounts create "$SERVICE" --display-name "TurfTimer MCP server"
gcloud projects add-iam-policy-binding "$PROJECT" --member "serviceAccount:$SA" --role roles/datastore.user --condition=None >/dev/null
# createCustomToken signs with the SA itself (signBlob)
gcloud iam service-accounts add-iam-policy-binding "$SA" --member "serviceAccount:$SA" --role roles/iam.serviceAccountTokenCreator >/dev/null

# 2) JWT signing secret (generated here, never printed)
if ! gcloud secrets describe "$SECRET" >/dev/null 2>&1; then
  openssl rand -base64 48 | tr -d '\n' | gcloud secrets create "$SECRET" --replication-policy automatic --data-file=-
fi
gcloud secrets add-iam-policy-binding "$SECRET" --member "serviceAccount:$SA" --role roles/secretmanager.secretAccessor >/dev/null

# 3) Deploy (source build via Dockerfile). Public ingress: auth is OAuth at the app layer.
URL="$(gcloud run services describe "$SERVICE" --region "$REGION" --format 'value(status.url)' 2>/dev/null || true)"
deploy() {
  gcloud run deploy "$SERVICE" --source . --region "$REGION" --service-account "$SA" \
    --allow-unauthenticated --min-instances 0 --max-instances 3 --concurrency 40 --memory 512Mi --cpu 1 \
    --set-secrets "MCP_JWT_SECRET=${SECRET}:latest" \
    --set-env-vars "FIREBASE_PROJECT_ID=${PROJECT},PUBLIC_BASE_URL=${1},FIREBASE_WEB_API_KEY=${FIREBASE_WEB_API_KEY}"
}
if [[ -z "$URL" ]]; then
  deploy "https://placeholder.invalid"
  URL="$(gcloud run services describe "$SERVICE" --region "$REGION" --format 'value(status.url)')"
fi
deploy "${PUBLIC_BASE_URL:-$URL}"

# 4) Firestore TTL policies (expired codes/grants are deleted automatically; reads also ignore them)
for c in mcp_auth_requests mcp_auth_codes mcp_link_codes mcp_grants; do
  gcloud firestore fields ttls update ttl --collection-group "$c" --enable-ttl --async >/dev/null || true
done

echo "Deployed: ${PUBLIC_BASE_URL:-$URL}/mcp"
echo "Check:    curl -s ${PUBLIC_BASE_URL:-$URL}/.well-known/oauth-authorization-server"
echo "Then set AiConnectConfig.BaseUrl in Services/AiConnectService.cs to ${PUBLIC_BASE_URL:-$URL} for the app build."
