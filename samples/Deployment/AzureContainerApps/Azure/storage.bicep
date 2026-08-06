param name string
param location string = resourceGroup().location
param clusteringTableName string = 'OrleansSiloInstances'

resource storage 'Microsoft.Storage/storageAccounts@2026-04-01' = {
  name: name
  location: location
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
  properties: {
    allowBlobPublicAccess: false
    allowCrossTenantReplication: false
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
    minimumTlsVersion: 'TLS1_2'
    publicNetworkAccess: 'Enabled'
    supportsHttpsTrafficOnly: true
  }
}

resource tableService 'Microsoft.Storage/storageAccounts/tableServices@2026-04-01' = {
  parent: storage
  name: 'default'
}

resource clusteringTable 'Microsoft.Storage/storageAccounts/tableServices/tables@2026-04-01' = {
  parent: tableService
  name: clusteringTableName
}

output storageId string = storage.id
output storageName string = storage.name
output tableServiceUri string = storage.properties.primaryEndpoints.table
output clusteringTableId string = clusteringTable.id
output clusteringTableName string = clusteringTable.name
