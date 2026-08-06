param name string
param location string = resourceGroup().location

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2024-11-30' = {
  name: name
  location: location
}

output clientId string = identity.properties.clientId
output id string = identity.id
output name string = identity.name
output principalId string = identity.properties.principalId
