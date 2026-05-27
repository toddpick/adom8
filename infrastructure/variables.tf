variable "resource_group_name" {
  description = "Name of the resource group"
  type        = string
  default     = "ai-agents-rg"
}

variable "location" {
  description = "Azure region for resources"
  type        = string
  default     = "eastus"
}

variable "static_web_app_location" {
  description = "Azure region for Static Web App (valid: westus2, centralus, eastus2, westeurope, eastasia)"
  type        = string
  default     = "eastus2"
}

variable "environment" {
  description = "Environment name (dev, staging, prod)"
  type        = string
  default     = "dev"
}

variable "function_app_name" {
  description = "Name of the Azure Function App (must be globally unique)"
  type        = string
}

variable "storage_account_name" {
  description = "Storage account name (must be globally unique, lowercase, no hyphens, max 24 chars)"
  type        = string
  validation {
    condition     = can(regex("^[a-z0-9]{3,24}$", var.storage_account_name))
    error_message = "Storage account name must be 3-24 lowercase alphanumeric characters."
  }
}

variable "static_web_app_name" {
  description = "Name of the Static Web App for dashboard"
  type        = string
  default     = "ai-agent-dashboard"
}

variable "alert_email" {
  description = "Email address for monitoring alert notifications"
  type        = string
}

variable "shared_service_bus_resource_group_name" {
  description = "Resource group containing the shared Service Bus namespace. Leave blank to disable Service Bus resources in Terraform."
  type        = string
  default     = ""
}

variable "shared_service_bus_namespace_name" {
  description = "Name of the shared Service Bus namespace. Leave blank to disable Service Bus resources in Terraform."
  type        = string
  default     = ""
}

variable "service_bus_topic_name" {
  description = "Per-project Service Bus topic name. Leave blank to disable Service Bus resources in Terraform."
  type        = string
  default     = ""
}

variable "service_bus_subscription_name" {
  description = "Per-project Service Bus subscription name. Leave blank to disable Service Bus resources in Terraform."
  type        = string
  default     = ""
}
