param projectId string
param sharedKeyVaultName string
param sharedServiceBusNamespace string
param projectFunctionPrincipalId string

var keyVaultSecretsUserRoleDefinitionId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
var serviceBusDataOwnerRoleDefinitionId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '090c5cfd-751d-490a-894a-3ce6f1109419')

resource sharedKeyVault 'Microsoft.KeyVault/vaults@2023-02-01' existing = {
  name: sharedKeyVaultName
}

resource serviceBus 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' existing = {
  name: sharedServiceBusNamespace
}

resource selfHealingTopic 'Microsoft.ServiceBus/namespaces/topics@2022-10-01-preview' = {
  parent: serviceBus
  name: 'adom8-${projectId}'
  properties: {
    defaultMessageTimeToLive: 'P14D'
    enablePartitioning: true
  }
}

resource projectKeyVaultSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(sharedKeyVault.id, projectId, keyVaultSecretsUserRoleDefinitionId)
  scope: sharedKeyVault
  properties: {
    roleDefinitionId: keyVaultSecretsUserRoleDefinitionId
    principalId: projectFunctionPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource projectServiceBusDataOwner 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(selfHealingTopic.id, projectId, serviceBusDataOwnerRoleDefinitionId)
  scope: selfHealingTopic
  properties: {
    roleDefinitionId: serviceBusDataOwnerRoleDefinitionId
    principalId: projectFunctionPrincipalId
    principalType: 'ServicePrincipal'
  }
}

output serviceBusTopicName string = selfHealingTopic.name
