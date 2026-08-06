param name string
param location string = resourceGroup().location
param containerAppEnvironmentId string
param repositoryImage string
param registryServer string
param runtimeIdentityId string
param envVars array = []
@minValue(1)
@maxValue(65535)
param advertisedSiloPort int
@minValue(1)
@maxValue(65535)
param advertisedGatewayPort int

var ingress = {
  external: true
  targetPort: 8080
  allowInsecure: false
  transport: 'auto'
  additionalPortMappings: [
    {
      external: true
      targetPort: 11111
      exposedPort: advertisedSiloPort
    }
    {
      external: true
      targetPort: 30000
      exposedPort: advertisedGatewayPort
    }
  ]
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
    maxReplicas: 1
    minReplicas: 1
    name: name
    registryServer: registryServer
    repositoryImage: repositoryImage
    runtimeIdentityId: runtimeIdentityId
  }
}

output fqdn string = containerApp.outputs.fqdn
output id string = containerApp.outputs.id
