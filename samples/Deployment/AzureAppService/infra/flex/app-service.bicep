param appName string
param location string
param productionSubnetId string
param stagingSubnetId string
param appInsightsConnectionString string
param storageTableServiceUri string
param serviceId string

@minValue(3)
param workerCount int = 3

var stagingSlotName = '${appName}stg'

resource applicationIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${appName}-identity'
  location: location
}

resource appServicePlan 'Microsoft.Web/serverfarms@2024-11-01' = {
  name: '${appName}-plan'
  location: location
  kind: 'app'
  sku: {
    name: 'P1v3'
    capacity: workerCount
  }
}

resource appService 'Microsoft.Web/sites@2024-11-01' = {
  name: appName
  location: location
  kind: 'app'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${applicationIdentity.id}': {}
    }
  }
  properties: {
    clientAffinityEnabled: true
    httpsOnly: true
    publicNetworkAccess: 'Enabled'
    serverFarmId: appServicePlan.id
    virtualNetworkSubnetId: productionSubnetId
    siteConfig: {
      alwaysOn: true
      ftpsState: 'Disabled'
      healthCheckPath: '/health/ready'
      http20Enabled: true
      minTlsVersion: '1.2'
      netFrameworkVersion: 'v10.0'
      numberOfWorkers: workerCount
      vnetPrivatePortsCount: 2
      webSocketsEnabled: true
      appSettings: [
        {
          name: 'AZURE_CLIENT_ID'
          value: applicationIdentity.properties.clientId
        }
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: appInsightsConnectionString
        }
        {
          name: 'ASPNETCORE_FORWARDEDHEADERS_ENABLED'
          value: 'true'
        }
        {
          name: 'ORLEANS_AZURE_STORAGE_URI'
          value: storageTableServiceUri
        }
        {
          name: 'ORLEANS_CLUSTER_ID'
          value: 'Default'
        }
        {
          name: 'ORLEANS_SERVICE_ID'
          value: serviceId
        }
        {
          name: 'WEBSITE_ADD_SITENAME_BINDINGS_IN_APPHOST_CONFIG'
          value: '1'
        }
        {
          name: 'WEBSITE_HEALTHCHECK_MAXPINGFAILURES'
          value: '2'
        }
        {
          name: 'WEBSITE_SWAP_WARMUP_PING_PATH'
          value: '/health/ready'
        }
        {
          name: 'WEBSITE_SWAP_WARMUP_PING_STATUSES'
          value: '200'
        }
        {
          name: 'WEBSITE_WARMUP_PATH'
          value: '/health/ready'
        }
        {
          name: 'WEBSITE_WARMUP_STATUSES'
          value: '200'
        }
      ]
    }
  }
}

resource stagingSlot 'Microsoft.Web/sites/slots@2024-11-01' = {
  parent: appService
  name: stagingSlotName
  location: location
  kind: 'app'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${applicationIdentity.id}': {}
    }
  }
  properties: {
    clientAffinityEnabled: true
    httpsOnly: true
    publicNetworkAccess: 'Enabled'
    serverFarmId: appServicePlan.id
    virtualNetworkSubnetId: stagingSubnetId
    siteConfig: {
      alwaysOn: true
      ftpsState: 'Disabled'
      healthCheckPath: '/health/ready'
      http20Enabled: true
      minTlsVersion: '1.2'
      netFrameworkVersion: 'v10.0'
      numberOfWorkers: workerCount
      vnetPrivatePortsCount: 2
      webSocketsEnabled: true
      appSettings: [
        {
          name: 'AZURE_CLIENT_ID'
          value: applicationIdentity.properties.clientId
        }
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: appInsightsConnectionString
        }
        {
          name: 'ASPNETCORE_FORWARDEDHEADERS_ENABLED'
          value: 'true'
        }
        {
          name: 'ORLEANS_AZURE_STORAGE_URI'
          value: storageTableServiceUri
        }
        {
          name: 'ORLEANS_CLUSTER_ID'
          value: 'Staging'
        }
        {
          name: 'ORLEANS_SERVICE_ID'
          value: serviceId
        }
        {
          name: 'WEBSITE_ADD_SITENAME_BINDINGS_IN_APPHOST_CONFIG'
          value: '1'
        }
        {
          name: 'WEBSITE_HEALTHCHECK_MAXPINGFAILURES'
          value: '2'
        }
        {
          name: 'WEBSITE_SWAP_WARMUP_PING_PATH'
          value: '/health/ready'
        }
        {
          name: 'WEBSITE_SWAP_WARMUP_PING_STATUSES'
          value: '200'
        }
        {
          name: 'WEBSITE_WARMUP_PATH'
          value: '/health/ready'
        }
        {
          name: 'WEBSITE_WARMUP_STATUSES'
          value: '200'
        }
      ]
    }
  }
}

resource slotConfig 'Microsoft.Web/sites/config@2024-11-01' = {
  parent: appService
  name: 'slotConfigNames'
  properties: {
    appSettingNames: [
      'AZURE_CLIENT_ID'
      'ORLEANS_CLUSTER_ID'
    ]
  }
}

output appName string = appService.name
output productionDefaultHostName string = appService.properties.defaultHostName
output applicationPrincipalId string = applicationIdentity.properties.principalId
output stagingDefaultHostName string = stagingSlot.properties.defaultHostName
output stagingSlotName string = stagingSlotName
