targetScope = 'subscription'

param resourceGroupName string = 'rg-adom8-shared'
param location string = 'westus2'
param onboardingFunctionAppName string = 'fa-adom8-registration'
param onboardingStaticWebAppName string = 'stapp-adom8-onboarding'
param sharedKeyVaultName string = 'kv-adom8-shared'
param sharedServiceBusNamespace string = 'sb-adom8-shared'

resource rg 'Microsoft.Resources/resourceGroups@2023-07-01' = {
  name: resourceGroupName
  location: location
}

module sharedResources './shared.resources.bicep' = {
  name: 'adom8-shared-resources'
  scope: rg
  params: {
    location: location
    onboardingFunctionAppName: onboardingFunctionAppName
    onboardingStaticWebAppName: onboardingStaticWebAppName
    sharedKeyVaultName: sharedKeyVaultName
    sharedServiceBusNamespace: sharedServiceBusNamespace
  }
}

output resourceGroupName string = rg.name
output onboardingFunctionAppName string = sharedResources.outputs.onboardingFunctionAppName
output onboardingStaticWebAppName string = sharedResources.outputs.onboardingStaticWebAppName
output sharedKeyVaultName string = sharedResources.outputs.sharedKeyVaultName
output sharedKeyVaultUrl string = sharedResources.outputs.sharedKeyVaultUrl
output sharedServiceBusNamespace string = sharedResources.outputs.sharedServiceBusNamespace
