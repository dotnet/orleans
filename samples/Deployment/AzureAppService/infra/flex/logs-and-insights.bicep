param operationalInsightsName string
param appInsightsName string
param location string

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: operationalInsightsName
  location: location
  properties: {
    retentionInDays: 30
    sku: {
      name: 'PerGB2018'
    }
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logs.id
  }
}

output appInsightsConnectionString string = appInsights.properties.ConnectionString
