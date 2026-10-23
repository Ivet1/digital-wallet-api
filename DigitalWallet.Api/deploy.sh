set -e

echo "==> 1. Building Docker image..."
docker build -t digital-wallet-api:latest .

echo "==> 2. Loading image into kind cluster..."
kind load docker-image digital-wallet-api:latest --name dev-cluster

echo "==> 3. Upgrading Helm release..."
helm upgrade digital-wallet-release ./digital-wallet-chart

echo "==> 4. Restarting deployment..."
kubectl rollout restart deployment digital-wallet-release-digital-wallet-chart

echo "==> Ready! Here is current pods status:"
kubectl get pods