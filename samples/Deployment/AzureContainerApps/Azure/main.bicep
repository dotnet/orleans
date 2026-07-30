param location string = resourceGroup().location

resource acr 'Microsoft.ContainerRegistry/registries@2021-09-01' = {
  name: toLower('${uniqueString(resourceGroup().id)}acr')
  location: location
  sku: {
    name: 'Basic'
  }
  properties: {
    adminUserEnabled: true
  }
}

module env 'environment.bicep' = {
  name: 'containerAppEnvironment'
  params: {
    location: location
  }
}

module storage 'storage.bicep' = {
  name: toLower('${uniqueString(resourceGroup().id)}strg')
  params: {
    location: location
  }
}

var shared_config = [
  {
    name: 'ASPNETCORE_ENVIRONMENT'
    value: 'Development'
  }
  {
    name: 'StorageConnectionString'
    value: format('DefaultEndpointsProtocol=https;AccountName=${storage.outputs.storageName};AccountKey=${storage.outputs.accountKey};EndpointSuffix=core.windows.net')
  }
  {
    name: 'APPINSIGHTS_INSTRUMENTATIONKEY'
    value: env.outputs.appInsightsInstrumentationKey
  }
  {
    name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
    value: env.outputs.appInsightsConnectionString
  }
]

module scaler 'scaler.bicep' = {
  name: 'scaler'
  params: {
    location: location
    name: 'scaler'
    containerAppEnvironmentId: env.outputs.id
    registry: acr.name
    registryPassword: acr.listCredentials().passwords[0].value
    registryUsername: acr.listCredentials().username
    envVars : shared_config
  }
}

module silo 'silo.bicep' = {
  name: 'silo'
  params: {
    location: location
    name: 'silo'
    containerAppEnvironmentId: env.outputs.id
    registry: acr.name
    registryPassword: acr.listCredentials().passwords[0].value
    registryUsername: acr.listCredentials().username
    envVars : shared_config
    scalerUrl: scaler.outputs.fqdn
  }
}

module dashboard 'dashboard.bicep' = {
  name: 'dashboard'
  params: {
    location: location
    name: 'dashboard'
    containerAppEnvironmentId: env.outputs.id
    registry: acr.name
    registryPassword: acr.listCredentials().passwords[0].value
    registryUsername: acr.listCredentials().username
    allowExternalIngress: true
    targetIngressPort: 8080
    maxReplicas: 1
    envVars : shared_config
  }
}

module minimalapiclient 'minimalapiclient.bicep' = {
  name: 'minimalapiclient'
  params: {
    location: location
    name: 'minimalapiclient'
    containerAppEnvironmentId: env.outputs.id
    registry: acr.name
    registryPassword: acr.listCredentials().passwords[0].value
    registryUsername: acr.listCredentials().username
    allowExternalIngress: true
    targetIngressPort: 80
    maxReplicas: 1
    envVars : shared_config
  }
}

module workerserviceclient 'workerserviceclient.bicep' = {
  name: 'workerserviceclient'
  params: {
    location: location
    name: 'workerserviceclient'
    containerAppEnvironmentId: env.outputs.id
    registry: acr.name
    registryPassword: acr.listCredentials().passwords[0].value
    registryUsername: acr.listCredentials().username
    maxReplicas: 1
    envVars : shared_config
  }
}

output acrLoginServer string = acr.properties.loginServer
