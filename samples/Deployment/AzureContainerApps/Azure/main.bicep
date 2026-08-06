targetScope = 'resourceGroup'

@minLength(3)
@maxLength(12)
param baseName string = 'orleansaca'

param location string = resourceGroup().location

@description('Immutable ACR image reference using an sha256 digest.')
param siloImage string

@description('Immutable ACR image reference using an sha256 digest.')
param dashboardImage string

@description('Immutable ACR image reference using an sha256 digest.')
param minimalApiClientImage string

@description('Immutable ACR image reference using an sha256 digest.')
param workerServiceClientImage string

@description('Immutable ACR image reference using an sha256 digest.')
param scalerImage string

var clusterId = '${baseName}-cluster'
var serviceId = 'azure-container-apps-sample'
var siloDefinitions = [
  {
    nameSuffix: 'silo-a'
    siloName: 'Silo-A'
    siloPort: 11111
    gatewayPort: 30000
  }
  {
    nameSuffix: 'silo-b'
    siloName: 'Silo-B'
    siloPort: 11112
    gatewayPort: 30001
  }
]

module foundation 'foundation.bicep' = {
  name: 'foundation'
  params: {
    baseName: baseName
    location: location
  }
}

module environment 'environment.bicep' = {
  name: 'containerAppEnvironment'
  params: {
    location: location
    name: '${foundation.outputs.resourcePrefix}-env'
    virtualNetworkName: '${foundation.outputs.resourcePrefix}-vnet'
  }
}

var sharedConfiguration = [
  {
    name: 'DOTNET_ENVIRONMENT'
    value: 'Production'
  }
  {
    name: 'ASPNETCORE_HTTP_PORTS'
    value: '8080'
  }
  {
    name: 'AzureTable__ServiceUri'
    value: foundation.outputs.tableServiceUri
  }
  {
    name: 'AZURE_CLIENT_ID'
    value: foundation.outputs.runtimeIdentityClientId
  }
  {
    name: 'Orleans__ClusterId'
    value: clusterId
  }
  {
    name: 'Orleans__ServiceId'
    value: serviceId
  }
  {
    name: 'Orleans__ClusteringTableName'
    value: foundation.outputs.clusteringTableName
  }
  {
    name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
    value: environment.outputs.appInsightsConnectionString
  }
]

module scaler 'scaler.bicep' = {
  name: 'scaler'
  params: {
    containerAppEnvironmentId: environment.outputs.id
    envVars: sharedConfiguration
    location: location
    name: '${foundation.outputs.resourcePrefix}-scaler'
    registryServer: foundation.outputs.registryLoginServer
    repositoryImage: scalerImage
    runtimeIdentityId: foundation.outputs.runtimeIdentityId
  }
}

module silos 'silo.bicep' = [for silo in siloDefinitions: {
  name: silo.nameSuffix
  params: {
    advertisedGatewayPort: silo.gatewayPort
    advertisedSiloPort: silo.siloPort
    containerAppEnvironmentId: environment.outputs.id
    envVars: concat(sharedConfiguration, [
      {
        name: 'Orleans__AdvertisedIPAddress'
        value: environment.outputs.staticIp
      }
      {
        name: 'Orleans__AdvertisedGatewayPort'
        value: string(silo.gatewayPort)
      }
      {
        name: 'Orleans__AdvertisedSiloPort'
        value: string(silo.siloPort)
      }
      {
        name: 'Orleans__SiloName'
        value: silo.siloName
      }
    ])
    location: location
    name: '${foundation.outputs.resourcePrefix}-${silo.nameSuffix}'
    registryServer: foundation.outputs.registryLoginServer
    repositoryImage: siloImage
    runtimeIdentityId: foundation.outputs.runtimeIdentityId
  }
}]

module dashboard 'dashboard.bicep' = {
  name: 'dashboard'
  params: {
    advertisedGatewayPort: 30002
    advertisedSiloPort: 11113
    containerAppEnvironmentId: environment.outputs.id
    envVars: concat(sharedConfiguration, [
      {
        name: 'Orleans__AdvertisedIPAddress'
        value: environment.outputs.staticIp
      }
      {
        name: 'Orleans__AdvertisedGatewayPort'
        value: '30002'
      }
      {
        name: 'Orleans__AdvertisedSiloPort'
        value: '11113'
      }
      {
        name: 'Orleans__SiloName'
        value: 'Dashboard'
      }
    ])
    location: location
    name: '${foundation.outputs.resourcePrefix}-dashboard'
    registryServer: foundation.outputs.registryLoginServer
    repositoryImage: dashboardImage
    runtimeIdentityId: foundation.outputs.runtimeIdentityId
  }
}

module minimalApiClient 'minimalapiclient.bicep' = {
  name: 'minimalApiClient'
  params: {
    containerAppEnvironmentId: environment.outputs.id
    envVars: sharedConfiguration
    location: location
    name: '${foundation.outputs.resourcePrefix}-api'
    registryServer: foundation.outputs.registryLoginServer
    repositoryImage: minimalApiClientImage
    runtimeIdentityId: foundation.outputs.runtimeIdentityId
  }
}

module workerServiceClient 'workerserviceclient.bicep' = {
  name: 'workerServiceClient'
  params: {
    containerAppEnvironmentId: environment.outputs.id
    envVars: sharedConfiguration
    location: location
    name: '${foundation.outputs.resourcePrefix}-worker'
    registryServer: foundation.outputs.registryLoginServer
    repositoryImage: workerServiceClientImage
    runtimeIdentityId: foundation.outputs.runtimeIdentityId
  }
}

output dashboardFqdn string = dashboard.outputs.fqdn
output environmentPrivateIp string = environment.outputs.staticIp
output minimalApiFqdn string = minimalApiClient.outputs.fqdn
output registryLoginServer string = foundation.outputs.registryLoginServer
output scalerFqdn string = scaler.outputs.fqdn
