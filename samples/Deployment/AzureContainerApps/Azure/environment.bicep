param name string
param location string = resourceGroup().location
param virtualNetworkName string

resource virtualNetwork 'Microsoft.Network/virtualNetworks@2025-07-01' = {
  name: virtualNetworkName
  location: location
  properties: {
    addressSpace: {
      addressPrefixes: [
        '10.42.0.0/23'
      ]
    }
  }
}

resource infrastructureSubnet 'Microsoft.Network/virtualNetworks/subnets@2025-07-01' = {
  parent: virtualNetwork
  name: 'container-apps-infrastructure'
  properties: {
    addressPrefix: '10.42.0.0/27'
    delegations: [
      {
        name: 'Microsoft.App.environments'
        properties: {
          serviceName: 'Microsoft.App/environments'
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

resource logs 'Microsoft.OperationalInsights/workspaces@2025-07-01' = {
  name: '${name}-logs'
  location: location
  properties: {
    retentionInDays: 30
    sku: {
      name: 'PerGB2018'
    }
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${name}-insights'
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logs.id
  }
}

resource environment 'Microsoft.App/managedEnvironments@2026-01-01' = {
  name: name
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'azure-monitor'
    }
    vnetConfiguration: {
      infrastructureSubnetId: infrastructureSubnet.id
      internal: true
    }
    workloadProfiles: [
      {
        name: 'Consumption'
        workloadProfileType: 'Consumption'
      }
    ]
    zoneRedundant: false
  }
}

resource environmentDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'container-app-logs'
  scope: environment
  properties: {
    logs: [
      {
        category: 'ContainerAppConsoleLogs'
        enabled: true
      }
      {
        category: 'ContainerAppSystemLogs'
        enabled: true
      }
    ]
    workspaceId: logs.id
  }
}

module privateDns 'private-dns.bicep' = {
  name: 'privateDns'
  params: {
    staticIp: environment.properties.staticIp
    virtualNetworkId: virtualNetwork.id
    zoneName: environment.properties.defaultDomain
  }
}

output appInsightsConnectionString string = appInsights.properties.ConnectionString
output defaultDomain string = environment.properties.defaultDomain
output id string = environment.id
output privateDnsZoneName string = privateDns.outputs.zoneName
output staticIp string = environment.properties.staticIp
