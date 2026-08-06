targetScope = 'resourceGroup'

@minLength(2)
@maxLength(16)
param appName string

param location string = resourceGroup().location
param serviceId string = 'ShoppingCartService'
param authenticationTenantId string
param authenticationClientId string

@secure()
param authenticationClientSecret string

@minValue(3)
param workerCount int = 3

param assignStorageRoles bool = true
param allowSharedKeyAccess bool = false

module deployment '../flex/main.bicep' = {
  name: 'orleansLinuxAppService'
  params: {
    appName: appName
    location: location
    serviceId: serviceId
    authenticationTenantId: authenticationTenantId
    authenticationClientId: authenticationClientId
    authenticationClientSecret: authenticationClientSecret
    operatingSystem: 'linux'
    workerCount: workerCount
    assignStorageRoles: assignStorageRoles
    allowSharedKeyAccess: allowSharedKeyAccess
  }
}

output appServiceName string = deployment.outputs.appServiceName
output productionDefaultHostName string = deployment.outputs.productionDefaultHostName
output stagingDefaultHostName string = deployment.outputs.stagingDefaultHostName
output stagingSlotName string = deployment.outputs.stagingSlotName
output storageAccountName string = deployment.outputs.storageAccountName
output operatingSystem string = deployment.outputs.operatingSystem
