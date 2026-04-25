param projectId string
param functionAppName string
param sharedKeyVaultName string
param sharedServiceBusNamespace string
param location string = 'westus2'

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: 'stadom8${take(replace(projectId, '-', ''), 15)}${uniqueString(resourceGroup().id)}'
  location: location
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
}

resource appServicePlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: 'asp-${projectId}'
  location: location
  sku: {
    name: 'Y1'
    tier: 'Dynamic'
  }
  kind: 'functionapp'
}

resource functionApp 'Microsoft.Web/sites@2023-12-01' = {
  name: functionAppName
  location: location
  kind: 'functionapp'
  properties: {
    serverFarmId: appServicePlan.id
    siteConfig: {
      appSettings: [
        {
          name: 'ADOM8_PROJECT_ID'
          value: projectId
        }
        {
          name: 'Onboarding__SharedKeyVaultName'
          value: sharedKeyVaultName
        }
        {
          name: 'Onboarding__SharedServiceBusNamespace'
          value: sharedServiceBusNamespace
        }
      ]
    }
  }
}
