locals {
  service_bus_enabled = (
    trimspace(var.shared_service_bus_resource_group_name) != ""
    && trimspace(var.shared_service_bus_namespace_name) != ""
    && trimspace(var.service_bus_topic_name) != ""
    && trimspace(var.service_bus_subscription_name) != ""
  )
}

data "azurerm_servicebus_namespace" "shared" {
  count = local.service_bus_enabled ? 1 : 0

  name                = var.shared_service_bus_namespace_name
  resource_group_name = var.shared_service_bus_resource_group_name
}

resource "azurerm_servicebus_topic" "project" {
  count = local.service_bus_enabled ? 1 : 0

  name         = var.service_bus_topic_name
  namespace_id = data.azurerm_servicebus_namespace.shared[0].id
}

resource "azurerm_servicebus_subscription" "project" {
  count = local.service_bus_enabled ? 1 : 0

  name               = var.service_bus_subscription_name
  topic_id           = azurerm_servicebus_topic.project[0].id
  max_delivery_count = 10
}

resource "azurerm_role_assignment" "service_bus_receiver" {
  count = local.service_bus_enabled ? 1 : 0

  scope                            = azurerm_servicebus_subscription.project[0].id
  role_definition_name             = "Azure Service Bus Data Receiver"
  principal_id                     = azurerm_windows_function_app.agents.identity[0].principal_id
  principal_type                   = "ServicePrincipal"
  skip_service_principal_aad_check = true
}
