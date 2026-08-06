param appName string
param location string
param productionSubnetId string
param stagingSubnetId string
param appInsightsConnectionString string
param storageTableServiceUri string
param serviceId string
param authenticationTenantId string
param authenticationClientId string

@secure()
param authenticationClientSecret string

@allowed([
  'windows'
  'linux'
])
param operatingSystem string = 'windows'

@minValue(3)
param workerCount int = 3

var isLinux = operatingSystem == 'linux'
var stagingSlotName = '${appName}stg'
var authenticationSecretSettingName = 'MICROSOFT_PROVIDER_AUTHENTICATION_SECRET'
var commonAppSettings = [
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
    name: authenticationSecretSettingName
    value: authenticationClientSecret
  }
  {
    name: 'ORLEANS_AZURE_STORAGE_URI'
    value: storageTableServiceUri
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
var commonSiteConfig = {
  alwaysOn: true
  ftpsState: 'Disabled'
  healthCheckPath: '/health/ready'
  http20Enabled: true
  minTlsVersion: '1.2'
  numberOfWorkers: workerCount
  vnetPrivatePortsCount: 1
  webSocketsEnabled: true
}
var platformSiteConfig = isLinux
  ? {
      linuxFxVersion: 'DOTNETCORE|10.0'
    }
  : {
      netFrameworkVersion: 'v10.0'
    }
var authenticationProperties = {
  globalValidation: {
    requireAuthentication: false
    unauthenticatedClientAction: 'AllowAnonymous'
  }
  httpSettings: {
    requireHttps: true
  }
  identityProviders: {
    azureActiveDirectory: {
      enabled: true
      registration: {
        clientId: authenticationClientId
        clientSecretSettingName: authenticationSecretSettingName
        openIdIssuer: '${environment().authentication.loginEndpoint}${authenticationTenantId}/v2.0'
      }
      validation: {
        allowedAudiences: [
          authenticationClientId
        ]
      }
    }
  }
  login: {
    tokenStore: {
      enabled: true
    }
  }
  platform: {
    enabled: true
    runtimeVersion: '~1'
  }
}

resource applicationIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${appName}-identity'
  location: location
}

resource appServicePlan 'Microsoft.Web/serverfarms@2024-11-01' = {
  name: '${appName}-plan'
  location: location
  kind: isLinux ? 'linux' : 'app'
  sku: {
    name: 'P1v3'
    capacity: workerCount
  }
  properties: {
    reserved: isLinux
  }
}

resource appService 'Microsoft.Web/sites@2024-11-01' = {
  name: appName
  location: location
  kind: isLinux ? 'app,linux' : 'app'
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
    siteConfig: union(
      commonSiteConfig,
      platformSiteConfig,
      {
        appSettings: concat(
          commonAppSettings,
          [
            {
              name: 'ORLEANS_CLUSTER_ID'
              value: 'Default'
            }
          ])
      })
  }
}

resource appServiceAuthentication 'Microsoft.Web/sites/config@2022-09-01' = {
  parent: appService
  name: 'authsettingsV2'
  properties: authenticationProperties
}

resource stagingSlot 'Microsoft.Web/sites/slots@2024-11-01' = {
  parent: appService
  name: stagingSlotName
  location: location
  kind: isLinux ? 'app,linux' : 'app'
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
    siteConfig: union(
      commonSiteConfig,
      platformSiteConfig,
      {
        appSettings: concat(
          commonAppSettings,
          [
            {
              name: 'ORLEANS_CLUSTER_ID'
              value: 'Staging'
            }
          ])
      })
  }
}

resource stagingAuthentication 'Microsoft.Web/sites/slots/config@2022-09-01' = {
  parent: stagingSlot
  name: 'authsettingsV2'
  properties: authenticationProperties
}

resource slotConfig 'Microsoft.Web/sites/config@2024-11-01' = {
  parent: appService
  name: 'slotConfigNames'
  properties: {
    appSettingNames: [
      'AZURE_CLIENT_ID'
      authenticationSecretSettingName
      'ORLEANS_CLUSTER_ID'
    ]
  }
}

output appName string = appService.name
output productionDefaultHostName string = appService.properties.defaultHostName
output applicationPrincipalId string = applicationIdentity.properties.principalId
output stagingDefaultHostName string = stagingSlot.properties.defaultHostName
output stagingSlotName string = stagingSlotName
