param resourceGroupName string = 'rg-adom8-shared'
param location string = 'westus2'

resource rg 'Microsoft.Resources/resourceGroups@2023-07-01' = {
  name: resourceGroupName
  location: location
}

module sharedResources './shared.resources.bicep' = {
  name: 'adom8-shared-resources'
  scope: rg
  params: {
    location: location
  }
}
