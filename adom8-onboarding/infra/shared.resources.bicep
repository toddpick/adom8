param location string = 'westus2'
param onboardingFunctionAppName string = 'fa-adom8-registration'
param onboardingStaticWebAppName string = 'stapp-adom8-onboarding'
param sharedKeyVaultName string = 'kv-adom8-shared'
param sharedServiceBusNamespace string = 'sb-adom8-shared'

var storageName = take('stadom8reg${uniqueString(resourceGroup().id)}', 24)
var sharedKeyVaultUrl = 'https://${sharedKeyVaultName}.vault.${environment().suffixes.keyvaultDns}'
var keyVaultSecretsOfficerRoleDefinitionId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7')

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageName
  location: location
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
}

resource keyVault 'Microsoft.KeyVault/vaults@2023-02-01' = {
  name: sharedKeyVaultName
  location: location
  properties: {
    tenantId: tenant().tenantId
    sku: {
      name: 'standard'
      family: 'A'
    }
    enableRbacAuthorization: true
  }
}

resource serviceBus 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' = {
  name: sharedServiceBusNamespace
  location: location
  sku: {
    name: 'Standard'
    tier: 'Standard'
  }
}

resource appServicePlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: 'asp-adom8-onboarding'
  location: location
  sku: {
    name: 'Y1'
    tier: 'Dynamic'
  }
  kind: 'functionapp'
}

resource onboardingFunctionApp 'Microsoft.Web/sites@2023-12-01' = {
  name: onboardingFunctionAppName
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
          name: 'Onboarding__SharedKeyVaultName'
          value: keyVault.name
        }
        {
          name: 'Onboarding__SharedKeyVaultUrl'
          value: sharedKeyVaultUrl
        }
        {
          name: 'Onboarding__SharedServiceBusNamespace'
          value: serviceBus.name
        }
      ]
    }
  }
}

resource onboardingKeyVaultSecretsOfficer 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, onboardingFunctionApp.name, keyVaultSecretsOfficerRoleDefinitionId)
  scope: keyVault
  properties: {
    roleDefinitionId: keyVaultSecretsOfficerRoleDefinitionId
    principalId: onboardingFunctionApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource onboardingStaticWebApp 'Microsoft.Web/staticSites@2023-12-01' = {
  name: onboardingStaticWebAppName
  location: location
  sku: {
    name: 'Free'
    tier: 'Free'
  }
  properties: {}
}

output onboardingFunctionAppName string = onboardingFunctionApp.name
output onboardingStaticWebAppName string = onboardingStaticWebApp.name
output sharedKeyVaultName string = keyVault.name
output sharedKeyVaultUrl string = sharedKeyVaultUrl
output sharedServiceBusNamespace string = serviceBus.name
