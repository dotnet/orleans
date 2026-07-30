param appName string
param location string
param vnetSubnetId string
param stagingSubnetId string
param appInsightsInstrumentationKey string
param appInsightsConnectionString string
param storageConnectionString string

resource appServicePlan 'Microsoft.Web/serverfarms@2021-03-01' = {
  name: '${appName}-plan'
  location: location
  kind: 'app'
  sku: {
    name: 'S1'
    capacity: 1
  }
}

resource appService 'Microsoft.Web/sites@2021-03-01' = {
  name: appName
  location: location
  kind: 'app'
  properties: {
    serverFarmId: appServicePlan.id
    virtualNetworkSubnetId: vnetSubnetId
    httpsOnly: true
    siteConfig: {
      vnetPrivatePortsCount: 2
      webSocketsEnabled: true
      netFrameworkVersion: 'v6.0'
      appSettings: [
        {
          name: 'APPINSIGHTS_INSTRUMENTATIONKEY'
          value: appInsightsInstrumentationKey
        }
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: appInsightsConnectionString
        }
        {
          name: 'ORLEANS_AZURE_STORAGE_CONNECTION_STRING'
          value: storageConnectionString
        }
        {
          name: 'ORLEANS_CLUSTER_ID'
          value: 'Default'
        }
      ]
      alwaysOn: true
    }
  }
}

resource stagingSlot 'Microsoft.Web/sites/slots@2022-03-01' = {
  name: '${appName}stg'
  location: location
  properties: {
    serverFarmId: appServicePlan.id
    virtualNetworkSubnetId: stagingSubnetId
    siteConfig: {
      http20Enabled: true
      vnetPrivatePortsCount: 2
      webSocketsEnabled: true
      netFrameworkVersion: 'v7.0'
      appSettings: [
        {
          name: 'APPINSIGHTS_INSTRUMENTATIONKEY'
          value: appInsightsInstrumentationKey
        }
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: appInsightsConnectionString
        }
        {
          name: 'ORLEANS_AZURE_STORAGE_CONNECTION_STRING'
          value: storageConnectionString
        }
        {
          name: 'ORLEANS_CLUSTER_ID'
          value: 'Staging'
        }
      ]
      alwaysOn: true
    }
  }
}

resource slotConfig 'Microsoft.Web/sites/config@2021-03-01' = {
  name: 'slotConfigNames'
  parent: appService
  properties: {
    appSettingNames: [
      'ORLEANS_CLUSTER_ID'
    ]
  }
}

resource appServiceConfig 'Microsoft.Web/sites/config@2021-03-01' = {
  parent: appService
  name: 'metadata'
  properties: {
    CURRENT_STACK: 'dotnet'
  }
}
