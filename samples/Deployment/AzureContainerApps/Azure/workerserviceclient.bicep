param name string
param location string = resourceGroup().location
param containerAppEnvironmentId string
param repositoryImage string
param registryServer string
param runtimeIdentityId string
param envVars array = []

module containerApp 'containerapp.bicep' = {
  name: '${name}-app'
  params: {
    containerAppEnvironmentId: containerAppEnvironmentId
    envVars: envVars
    location: location
    maxReplicas: 1
    minReplicas: 1
    name: name
    registryServer: registryServer
    repositoryImage: repositoryImage
    runtimeIdentityId: runtimeIdentityId
  }
}

output id string = containerApp.outputs.id
