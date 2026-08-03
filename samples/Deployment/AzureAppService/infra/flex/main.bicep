targetScope = 'resourceGroup'

@minLength(2)
@maxLength(16)
param appName string

param location string = resourceGroup().location
param serviceId string = 'ShoppingCartService'

@minValue(3)
param workerCount int = 3

param assignStorageRoles bool = true
param allowSharedKeyAccess bool = false

var storageName = '${replace(toLower(appName), '-', '')}storage'

resource vnet 'Microsoft.Network/virtualNetworks@2024-05-01' = {
  name: '${appName}-vnet'
  location: location
  properties: {
    addressSpace: {
      addressPrefixes: [
        '172.17.0.0/16'
        '192.168.0.0/16'
      ]
    }
    subnets: [
      {
        name: 'default'
        properties: {
          addressPrefix: '172.17.0.0/24'
          delegations: [
            {
              name: 'app-service'
              properties: {
                serviceName: 'Microsoft.Web/serverFarms'
              }
            }
          ]
          serviceEndpoints: [
            {
              service: 'Microsoft.Storage'
            }
          ]
        }
      }
      {
        name: 'staging'
        properties: {
          addressPrefix: '192.168.0.0/24'
          delegations: [
            {
              name: 'app-service'
              properties: {
                serviceName: 'Microsoft.Web/serverFarms'
              }
            }
          ]
          serviceEndpoints: [
            {
              service: 'Microsoft.Storage'
            }
          ]
        }
      }
    ]
  }
}

module storageModule 'storage.bicep' = {
  name: 'orleansStorageModule'
  params: {
    name: storageName
    location: location
    allowSharedKeyAccess: allowSharedKeyAccess
    allowedSubnetIds: [
      vnet.properties.subnets[0].id
      vnet.properties.subnets[1].id
    ]
  }
}

module logsModule 'logs-and-insights.bicep' = {
  name: 'orleansLogModule'
  params: {
    operationalInsightsName: '${appName}-logs'
    appInsightsName: '${appName}-insights'
    location: location
  }
}

module appServiceModule 'app-service.bicep' = {
  name: 'orleansAppServiceModule'
  params: {
    appName: appName
    location: location
    productionSubnetId: vnet.properties.subnets[0].id
    stagingSubnetId: vnet.properties.subnets[1].id
    appInsightsConnectionString: logsModule.outputs.appInsightsConnectionString
    storageTableServiceUri: storageModule.outputs.tableServiceUri
    serviceId: serviceId
    workerCount: workerCount
  }
}

module applicationStorageRole 'storage-role.bicep' = if (assignStorageRoles) {
  name: 'applicationStorageRole'
  params: {
    storageAccountName: storageName
    principalId: appServiceModule.outputs.applicationPrincipalId
    assignmentPurpose: 'application'
  }
}

output appServiceName string = appServiceModule.outputs.appName
output productionDefaultHostName string = appServiceModule.outputs.productionDefaultHostName
output stagingDefaultHostName string = appServiceModule.outputs.stagingDefaultHostName
output stagingSlotName string = appServiceModule.outputs.stagingSlotName
output storageAccountName string = storageModule.outputs.storageAccountName
