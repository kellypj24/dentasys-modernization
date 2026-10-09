variable "location" {
  description = "Azure region. B1ms is offered in most regions."
  type        = string
  default     = "eastus2"
}

variable "operator_ip" {
  description = "The one public IPv4 address allowed through the Postgres firewall."
  type        = string

  validation {
    condition     = can(regex("^(\\d{1,3}\\.){3}\\d{1,3}$", var.operator_ip))
    error_message = "operator_ip must be a single IPv4 address, not a range."
  }
}

variable "expires_on" {
  description = "YYYY-MM-DD. Tagged on every resource; the date this lab should be gone by."
  type        = string

  validation {
    condition     = can(regex("^\\d{4}-\\d{2}-\\d{2}$", var.expires_on))
    error_message = "expires_on must be YYYY-MM-DD."
  }
}

variable "budget_alert_email" {
  description = "Optional. Creates a resource-group budget that emails at the threshold. Alerts only; it does not stop spend."
  type        = string
  default     = null
}

variable "budget_amount" {
  description = "Monthly budget, in the billing currency."
  type        = number
  default     = 5
}

variable "api_image" {
  description = "API container image, e.g. ghcr.io/<owner>/dentasys-api:<tag>. Null deploys the database only."
  type        = string
  default     = null
}

variable "worker_image" {
  description = "Worker container image. Null deploys the database only."
  type        = string
  default     = null
}

variable "outbox_cron" {
  description = "How often the outbox job runs. Each run drains until empty, then exits."
  type        = string
  default     = "*/5 * * * *"
}
