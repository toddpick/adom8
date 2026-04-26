targetScope = 'subscription'

param projectId string
param location string = 'westus2'
param sharedResourceGroupName string = 'rg-adom8-shared'
param sharedKeyVaultName string = 'kv-adom8-shared'
param sharedServiceBusNamespace string = 'sb-adom8-shared'

var resourceGroupName = 'rg-adom8-${projectId}'
var functionAppName = 'fa-adom8-${projectId}'

resource rg 'Microsoft.Resources/resourceGroups@2023-07-01' = {
  name: resourceGroupName
  location: location
}

module projectResources './project.resources.bicep' = {
  name: 'adom8-project-${projectId}'
  scope: rg
  params: {
    projectId: projectId
    functionAppName: functionAppName
    sharedKeyVaultName: sharedKeyVaultName
    sharedServiceBusNamespace: sharedServiceBusNamespace
    location: location
  }
}

module projectMessaging './project.messaging.bicep' = {
  name: 'adom8-project-messaging-${projectId}'
  scope: resourceGroup(sharedResourceGroupName)
  params: {
    projectId: projectId
    sharedKeyVaultName: sharedKeyVaultName
    sharedServiceBusNamespace: sharedServiceBusNamespace
    projectFunctionPrincipalId: projectResources.outputs.functionPrincipalId
  }
}

output resourceGroupName string = rg.name
output functionAppName string = projectResources.outputs.functionAppName
output storageAccountName string = projectResources.outputs.storageAccountName
output serviceBusTopicName string = projectMessaging.outputs.serviceBusTopicName
