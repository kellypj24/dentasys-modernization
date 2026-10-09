# The API and the outbox worker, sized so the lab stays inside the Container Apps
# monthly free grant:
#
#   api     scales to zero; ingress admits operator_ip only, so nothing else can
#           wake it.
#   worker  a scheduled job, not an always-on app. A 2-second poller running all
#           month uses ~650k vCPU-seconds at 0.25 vCPU, over three times the free
#           grant. A job running `--once` every 5 minutes uses a few percent of it.
#
# No Log Analytics workspace: a Container Apps environment without one sends logs
# nowhere, and the workspace is the usual surprise line on the bill.

locals {
  deploy_apps = var.api_image != null && var.worker_image != null

  target_connection = join(";", [
    "Host=${azurerm_postgresql_flexible_server.target.fqdn}",
    "Port=5432",
    "Database=${azurerm_postgresql_flexible_server_database.dentasys.name}",
    "Username=${azurerm_postgresql_flexible_server.target.administrator_login}",
    "Password=${random_password.pg_admin.result}",
    "SSL Mode=Require",
  ])
}

# Container Apps consumption egress has no fixed IP, so Postgres has to admit
# Azure-hosted addresses. 0.0.0.0 is Azure's sentinel for exactly that. It is
# wider than one IP; the server still requires the password and TLS.
resource "azurerm_postgresql_flexible_server_firewall_rule" "azure_services" {
  count = local.deploy_apps ? 1 : 0

  name             = "azure-services"
  server_id        = azurerm_postgresql_flexible_server.target.id
  start_ip_address = "0.0.0.0"
  end_ip_address   = "0.0.0.0"
}

resource "azurerm_container_app_environment" "lab" {
  count = local.deploy_apps ? 1 : 0

  name                = "cae-${local.name}"
  location            = azurerm_resource_group.lab.location
  resource_group_name = azurerm_resource_group.lab.name
  tags                = local.tags
}

resource "azurerm_container_app" "api" {
  count = local.deploy_apps ? 1 : 0

  name                         = "api-${random_string.suffix.result}"
  container_app_environment_id = azurerm_container_app_environment.lab[0].id
  resource_group_name          = azurerm_resource_group.lab.name
  revision_mode                = "Single"
  tags                         = local.tags

  secret {
    name  = "target-connection"
    value = local.target_connection
  }

  ingress {
    external_enabled = true
    target_port      = 8080

    ip_security_restriction {
      name             = "operator"
      action           = "Allow"
      ip_address_range = "${var.operator_ip}/32"
    }

    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }

  template {
    min_replicas = 0
    max_replicas = 1

    container {
      name   = "api"
      image  = var.api_image
      cpu    = 0.25
      memory = "0.5Gi"

      env {
        name        = "DENTASYS_TARGET_CONNECTION"
        secret_name = "target-connection"
      }
    }
  }
}

resource "azurerm_container_app_job" "outbox" {
  count = local.deploy_apps ? 1 : 0

  name                         = "outbox-${random_string.suffix.result}"
  location                     = azurerm_resource_group.lab.location
  resource_group_name          = azurerm_resource_group.lab.name
  container_app_environment_id = azurerm_container_app_environment.lab[0].id
  replica_timeout_in_seconds   = 300
  replica_retry_limit          = 0
  tags                         = local.tags

  schedule_trigger_config {
    cron_expression          = var.outbox_cron
    parallelism              = 1
    replica_completion_count = 1
  }

  secret {
    name  = "target-connection"
    value = local.target_connection
  }

  template {
    container {
      name   = "outbox"
      image  = var.worker_image
      args   = ["--once"]
      cpu    = 0.25
      memory = "0.5Gi"

      env {
        name        = "DENTASYS_TARGET_CONNECTION"
        secret_name = "target-connection"
      }
    }
  }
}
