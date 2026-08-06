targetScope = 'resourceGroup'

@minLength(3)
@maxLength(12)
@description('Lowercase letters, numbers, and hyphens used as the resource-name prefix.')
param baseName string = 'orleansaca'

param location string = resourceGroup().location
param clusteringTableName string = 'OrleansSiloInstances'

var normalizedBaseName = toLower(baseName)
var compactBaseName = replace(normalizedBaseName, '-', '')
var suffix = take(uniqueString(subscription().subscriptionId, resourceGroup().id, normalizedBaseName), 6)
var resourcePrefix = '${normalizedBaseName}-${suffix}'
var compactResourcePrefix = take('${compactBaseName}${suffix}', 19)

module registry 'registry.bicep' = {
  name: 'containerRegistry'
  params: {
    location: location
    name: '${compactResourcePrefix}acr'
  }
}

module storage 'storage.bicep' = {
  name: 'membershipStorage'
  params: {
    clusteringTableName: clusteringTableName
    location: location
    name: '${compactResourcePrefix}store'
  }
}

module identity 'identity.bicep' = {
  name: 'runtimeIdentity'
  params: {
    location: location
    name: '${resourcePrefix}-id'
  }
}

output clusteringTableId string = storage.outputs.clusteringTableId
output clusteringTableName string = storage.outputs.clusteringTableName
output registryId string = registry.outputs.id
output registryLoginServer string = registry.outputs.loginServer
output registryName string = registry.outputs.name
output resourcePrefix string = resourcePrefix
output runtimeIdentityClientId string = identity.outputs.clientId
output runtimeIdentityId string = identity.outputs.id
output runtimeIdentityName string = identity.outputs.name
output runtimeIdentityPrincipalId string = identity.outputs.principalId
output storageName string = storage.outputs.storageName
output tableServiceUri string = storage.outputs.tableServiceUri
