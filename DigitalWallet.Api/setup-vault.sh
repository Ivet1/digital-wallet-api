set -e

echo "Waiting for vault-0 pod to be ready..."
kubectl wait --for=condition=Ready pod/vault-0 --timeout=60s

echo "Waiting for Vault service to respond..."
until kubectl exec vault-0 -- vault status -format=json >/dev/null 2>&1; do
  sleep 2
done

echo "Enabling Kubernetes authentication..."
kubectl exec vault-0 -- vault auth enable kubernetes || true

echo "Configuring Kubernetes auth host..."
kubectl exec vault-0 -- vault write auth/kubernetes/config \
    kubernetes_host="https://kubernetes.default.svc"

echo "Creating Vault policy..."
kubectl exec -i vault-0 -- vault policy write digital-wallet-policy - <<EOF_POLICY
path "secret/data/digital-wallet/*" {
  capabilities = ["read"]
}
EOF_POLICY

echo "Creating Vault Kubernetes role..."
kubectl exec vault-0 -- vault write auth/kubernetes/role/digital-wallet-role \
    bound_service_account_names=digital-wallet-app-digital-wallet-chart \
    bound_service_account_namespaces=default \
    policies=digital-wallet-policy \
    ttl=1h

echo "Seeding PostgreSQL secret in Vault..."
kubectl exec vault-0 -- vault kv put secret/digital-wallet/postgres POSTGRES_PASSWORD=postgres || true

echo "Vault setup completed successfully."
