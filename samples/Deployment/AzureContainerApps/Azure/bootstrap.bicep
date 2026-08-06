targetScope = 'resourceGroup'

@minLength(3)
@maxLength(12)
param baseName string = 'orleansaca'

param location string = resourceGroup().location

@description('Object ID of the service principal used by the routine GitHub Actions deployment.')
param deploymentPrincipalId string

var normalizedBaseName = toLower(baseName)
var compactBaseName = replace(normalizedBaseName, '-', '')
var suffix = take(uniqueString(subscription().subscriptionId, resourceGroup().id, normalizedBaseName), 6)
var resourcePrefix = '${normalizedBaseName}-${suffix}'
var compactResourcePrefix = take('${compactBaseName}${suffix}', 19)
var registryName = '${compactResourcePrefix}acr'
var runtimeIdentityName = '${resourcePrefix}-id'
var storageName = '${compactResourcePrefix}store'
var clusteringTableName = 'OrleansSiloInstances'

module foundation 'foundation.bicep' = {
  name: 'foundation'
  params: {
    baseName: baseName
    location: location
  }
}

resource registry 'Microsoft.ContainerRegistry/registries@2025-11-01' existing = {
  name: registryName
}

resource storage 'Microsoft.Storage/storageAccounts@2026-04-01' existing = {
  name: storageName
}

resource tableService 'Microsoft.Storage/storageAccounts/tableServices@2026-04-01' existing = {
  parent: storage
  name: 'default'
}

resource clusteringTable 'Microsoft.Storage/storageAccounts/tableServices/tables@2026-04-01' existing = {
  parent: tableService
  name: clusteringTableName
}

resource runtimeIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' existing = {
  name: runtimeIdentityName
}

var acrPullRoleId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '7f951dda-4ed3-4680-a7ca-43fe172d538d')
var acrPushRoleId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '8311e382-0749-4cb8-b61a-304f252e45ec')
var contributorRoleId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  'b24988ac-6180-42a0-ab88-20f7382dd24c')
var storageTableDataContributorRoleId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3')

resource runtimeAcrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, runtimeIdentity.id, acrPullRoleId)
  scope: registry
  dependsOn: [
    foundation
  ]
  properties: {
    principalId: runtimeIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: acrPullRoleId
  }
}

resource runtimeTableAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(clusteringTable.id, runtimeIdentity.id, storageTableDataContributorRoleId)
  scope: clusteringTable
  dependsOn: [
    foundation
  ]
  properties: {
    principalId: runtimeIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: storageTableDataContributorRoleId
  }
}

resource deploymentContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, deploymentPrincipalId, contributorRoleId)
  properties: {
    principalId: deploymentPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: contributorRoleId
  }
}

resource deploymentAcrPush 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, deploymentPrincipalId, acrPushRoleId)
  scope: registry
  dependsOn: [
    foundation
  ]
  properties: {
    principalId: deploymentPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: acrPushRoleId
  }
}

output registryLoginServer string = foundation.outputs.registryLoginServer
output runtimeIdentityClientId string = foundation.outputs.runtimeIdentityClientId
