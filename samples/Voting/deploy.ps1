$resourceGroup = "votingapp"
$location = "westus"
$clusterName = "votingapp"
$containerRegistry = "dncvotingapp"
$repositoryRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$dockerfile = Join-Path $PSScriptRoot "Dockerfile"
$deployment = Join-Path $PSScriptRoot "deployment.yaml"

$acrLoginServer = $(az acr show --name $containerRegistry --resource-group $resourceGroup --query loginServer).Trim('"')
az acr login --name $containerRegistry

docker build $repositoryRoot -f $dockerfile -t $acrLoginServer/votingapp &&
docker push $acrLoginServer/votingapp &&
kubectl apply -f $deployment &&
kubectl rollout restart deployment/votingapp
