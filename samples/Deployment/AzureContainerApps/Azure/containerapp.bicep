param name string
param location string = resourceGroup().location
param containerAppEnvironmentId string
param repositoryImage string
param registryServer string
param runtimeIdentityId string
param envVars array = []
param ingress object = {}
@minValue(1)
param minReplicas int = 1
@minValue(1)
param maxReplicas int = 1
param healthPort int = 8080
param cpu string = '0.5'
param memory string = '1Gi'

var healthProbes = [
  {
    type: 'Startup'
    failureThreshold: 30
    httpGet: {
      path: '/health/startup'
      port: healthPort
      scheme: 'HTTP'
    }
    initialDelaySeconds: 1
    periodSeconds: 5
    successThreshold: 1
    timeoutSeconds: 3
  }
  {
    type: 'Readiness'
    failureThreshold: 6
    httpGet: {
      path: '/health/ready'
      port: healthPort
      scheme: 'HTTP'
    }
    initialDelaySeconds: 5
    periodSeconds: 5
    successThreshold: 1
    timeoutSeconds: 3
  }
  {
    type: 'Liveness'
    failureThreshold: 3
    httpGet: {
      path: '/health/live'
      port: healthPort
      scheme: 'HTTP'
    }
    initialDelaySeconds: 30
    periodSeconds: 10
    successThreshold: 1
    timeoutSeconds: 3
  }
]

resource containerApp 'Microsoft.App/containerApps@2026-01-01' = {
  name: name
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${runtimeIdentityId}': {}
    }
  }
  properties: {
    environmentId: containerAppEnvironmentId
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: empty(ingress) ? null : ingress
      maxInactiveRevisions: 2
      registries: [
        {
          identity: runtimeIdentityId
          server: registryServer
        }
      ]
    }
    template: {
      terminationGracePeriodSeconds: 60
      containers: [
        {
          name: name
          image: repositoryImage
          env: envVars
          probes: healthProbes
          resources: {
            cpu: json(cpu)
            memory: memory
          }
        }
      ]
      scale: {
        minReplicas: minReplicas
        maxReplicas: maxReplicas
      }
    }
  }
}

output fqdn string = empty(ingress) ? '' : containerApp.properties.configuration.ingress.fqdn
output id string = containerApp.id
