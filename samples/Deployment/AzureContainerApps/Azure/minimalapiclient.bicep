param name string
param location string = resourceGroup().location
param containerAppEnvironmentId string
param repositoryImage string
param registryServer string
param runtimeIdentityId string
param envVars array = []

var ingress = {
  external: true
  targetPort: 8080
  allowInsecure: false
  transport: 'auto'
  traffic: [
    {
      latestRevision: true
      weight: 100
    }
  ]
}

module containerApp 'containerapp.bicep' = {
  name: '${name}-app'
  params: {
    containerAppEnvironmentId: containerAppEnvironmentId
    envVars: envVars
    ingress: ingress
    location: location
    maxReplicas: 2
    minReplicas: 1
    name: name
    registryServer: registryServer
    repositoryImage: repositoryImage
    runtimeIdentityId: runtimeIdentityId
  }
}

output fqdn string = containerApp.outputs.fqdn
output id string = containerApp.outputs.id
