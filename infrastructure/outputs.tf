output "resource_group_name" {
  description = "Resource group name"
  value       = azurerm_resource_group.ai_agents.name
}

output "function_app_name" {
  description = "Function App name"
  value       = azurerm_windows_function_app.agents.name
}

output "function_app_url" {
  description = "Function App URL"
  value       = "https://${azurerm_windows_function_app.agents.default_hostname}"
}

output "orchestrator_webhook_url" {
  description = "URL for Azure DevOps Service Hook"
  value       = "https://${azurerm_windows_function_app.agents.default_hostname}/api/OrchestratorWebhook"
}

output "dashboard_url" {
  description = "Dashboard URL"
  value       = "https://${azurerm_static_web_app.dashboard.default_host_name}"
}

output "dashboard_api_key" {
  description = "Static Web App deployment token"
  value       = azurerm_static_web_app.dashboard.api_key
  sensitive   = true
}

output "storage_connection_string" {
  description = "Storage account connection string"
  value       = azurerm_storage_account.functions.primary_connection_string
  sensitive   = true
}

output "service_bus_namespace_fqdn" {
  description = "Shared Service Bus namespace FQDN"
  value       = local.service_bus_enabled ? "${var.shared_service_bus_namespace_name}.servicebus.windows.net" : ""
}

output "service_bus_topic_name" {
  description = "Per-project Service Bus topic name"
  value       = local.service_bus_enabled ? azurerm_servicebus_topic.project[0].name : ""
}

output "service_bus_subscription_name" {
  description = "Per-project Service Bus subscription name"
  value       = local.service_bus_enabled ? azurerm_servicebus_subscription.project[0].name : ""
}

output "next_steps" {
  description = "Post-deployment instructions"
  value       = <<-EOT
  
  ========================================
  INFRASTRUCTURE DEPLOYED SUCCESSFULLY
  ========================================
  
  NEXT STEPS:
  
  1. Configure Function App Settings:
     az functionapp config appsettings set \
       --name ${azurerm_windows_function_app.agents.name} \
       --resource-group ${azurerm_resource_group.ai_agents.name} \
       --settings \
         "AI__ApiKey=YOUR_CLAUDE_OR_OPENAI_KEY" \
         "AzureDevOps__OrganizationUrl=https://dev.azure.com/yourorg" \
         "AzureDevOps__Pat=YOUR_AZURE_DEVOPS_PAT" \
         "AzureDevOps__Project=YourProject" \
         "Git__RepositoryUrl=YOUR_GIT_REPO_URL" \
         "Git__Token=YOUR_GIT_PAT"
  
  2. Deploy Function Code:
     cd src/AIAgents.Functions
     func azure functionapp publish ${azurerm_windows_function_app.agents.name}
  
  3. Configure Azure DevOps Service Hook:
     - Project Settings > Service Hooks > Web Hooks
     - Trigger: Work item updated (state field)
     - URL: https://${azurerm_windows_function_app.agents.default_hostname}/api/OrchestratorWebhook
  
  4. Deploy Dashboard:
     See .github/workflows/deploy-dashboard.yml
  
  Dashboard: https://${azurerm_static_web_app.dashboard.default_host_name}
  Functions: https://${azurerm_windows_function_app.agents.default_hostname}
  
  EOT
}
