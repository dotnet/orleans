param name string
param location string = resourceGroup().location
param containerAppEnvironmentId string
param repositoryImage string = 'mcr.microsoft.com/azuredocs/containerapps-helloworld:latest'
param envVars array = []
param registry string
param registryUsername string
param minReplicas int = 1
param maxReplicas int = 10
param scalerUrl string
@secure()
param registryPassword string

resource containerApp 'Microsoft.App/containerApps@2022-01-01-preview' ={
  name: name
  location: location
  properties: {
    managedEnvironmentId: containerAppEnvironmentId
    configuration: {
      activeRevisionsMode: 'multiple'
      secrets: [
        {
          name: 'container-registry-password'
          value: registryPassword
        }
      ]
      registries: [
        {
          server: registry
          username: registryUsername
          passwordSecretRef: 'container-registry-password'
        }
      ]
    }
    template: {
      containers: [
        {
          image: repositoryImage
          name: name
          env: envVars
        }
      ]
      scale: {
        minReplicas: minReplicas
        maxReplicas: maxReplicas
        rules: [
          {
            name: 'scaler'
            custom: {
              type: 'external'
              metadata: {
                scalerAddress: '${scalerUrl}:80'
                graintype: 'sensortwin'
                siloNameFilter: 'silo'
                upperbound: '300'
              }
            }
          }
        ]
      }
    }
  }
}
