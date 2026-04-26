param projectId string
param functionAppName string
param sharedKeyVaultName string
param sharedServiceBusNamespace string
param location string = 'westus2'

var storageName = take('stadom8${replace(toLower(projectId), '-', '')}${uniqueString(resourceGroup().id)}', 24)
var sharedKeyVaultUrl = 'https://${sharedKeyVaultName}.vault.${environment().suffixes.keyvaultDns}'
var serviceBusTopicName = 'adom8-${projectId}'

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageName
  location: location
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
}

resource agentTasksQueue 'Microsoft.Storage/storageAccounts/queueServices/queues@2023-05-01' = {
  name: '${storage.name}/default/agent-tasks'
}

resource poisonQueue 'Microsoft.Storage/storageAccounts/queueServices/queues@2023-05-01' = {
  name: '${storage.name}/default/agent-tasks-poison'
}

resource activityLogTable 'Microsoft.Storage/storageAccounts/tableServices/tables@2023-05-01' = {
  name: '${storage.name}/default/activitylog'
}

resource tempReposContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  name: '${storage.name}/default/temp-repos'
  properties: {
    publicAccess: 'None'
  }
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
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    httpsOnly: true
    serverFarmId: appServicePlan.id
    siteConfig: {
      appSettings: [
        {
          name: 'AzureWebJobsStorage'
          value: 'DefaultEndpointsProtocol=https;AccountName=${storage.name};AccountKey=${storage.listKeys().keys[0].value};EndpointSuffix=${environment().suffixes.storage}'
        }
        {
          name: 'FUNCTIONS_EXTENSION_VERSION'
          value: '~4'
        }
        {
          name: 'FUNCTIONS_WORKER_RUNTIME'
          value: 'dotnet-isolated'
        }
        {
          name: 'ADOM8_PROJECT_ID'
          value: projectId
        }
        {
          name: 'Onboarding__SharedKeyVaultName'
          value: sharedKeyVaultName
        }
        {
          name: 'Onboarding__SharedKeyVaultUrl'
          value: sharedKeyVaultUrl
        }
        {
          name: 'Onboarding__SharedServiceBusNamespace'
          value: sharedServiceBusNamespace
        }
        {
          name: 'Onboarding__SelfHealingTopicName'
          value: serviceBusTopicName
        }
      ]
    }
  }
}

output functionAppName string = functionApp.name
output functionPrincipalId string = functionApp.identity.principalId
output storageAccountName string = storage.name
