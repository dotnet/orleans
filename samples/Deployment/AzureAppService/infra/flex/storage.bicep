param name string
param location string
param allowedSubnetIds array
param allowSharedKeyAccess bool = false

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: name
  location: location
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
  properties: {
    allowBlobPublicAccess: false
    allowSharedKeyAccess: allowSharedKeyAccess
    defaultToOAuthAuthentication: true
    minimumTlsVersion: 'TLS1_2'
    publicNetworkAccess: 'Enabled'
    supportsHttpsTrafficOnly: true
    networkAcls: {
      bypass: 'None'
      defaultAction: 'Deny'
      ipRules: []
      virtualNetworkRules: [
        for subnetId in allowedSubnetIds: {
          action: 'Allow'
          id: subnetId
        }
      ]
    }
  }
}

output storageAccountId string = storage.id
output storageAccountName string = storage.name
output tableServiceUri string = storage.properties.primaryEndpoints.table
