# The Azure target for the modernization: PostgreSQL Flexible Server sized to
# the free-account allowance (B1ms, 32 GB, no HA, no geo-backup, no auto-grow).
# tests/free_tier.tftest.hcl fails if any of those drift.

resource "random_string" "suffix" {
  length  = 5
  upper   = false
  special = false
}

locals {
  name = "dentasys-lab-${random_string.suffix.result}"

  tags = {
    project    = "dentasys-modernization"
    managed_by = "terraform"
    expires_on = var.expires_on
  }
}

resource "azurerm_resource_group" "lab" {
  name     = "rg-${local.name}"
  location = var.location
  tags     = local.tags
}

resource "random_password" "pg_admin" {
  length           = 32
  special          = true
  override_special = "-_"
  min_upper        = 1
  min_lower        = 1
  min_numeric      = 1
}

resource "azurerm_postgresql_flexible_server" "target" {
  name                = "pg-${local.name}"
  resource_group_name = azurerm_resource_group.lab.name
  location            = azurerm_resource_group.lab.location
  version             = "17"

  # The free allowance is 750 h/month of B1ms plus 32 GB storage. Auto-grow off:
  # a disk that grows past 32 GB starts billing and never shrinks back.
  sku_name          = "B_Standard_B1ms"
  storage_mb        = 32768
  auto_grow_enabled = false

  backup_retention_days        = 7
  geo_redundant_backup_enabled = false

  public_network_access_enabled = true
  administrator_login           = "dentasys"
  administrator_password        = random_password.pg_admin.result

  authentication {
    password_auth_enabled         = true
    active_directory_auth_enabled = false
  }

  tags = local.tags

  lifecycle {
    # Azure assigns a zone when none is given; without this every plan wants to move it.
    ignore_changes = [zone]
  }
}

resource "azurerm_postgresql_flexible_server_database" "dentasys" {
  name      = "dentasys"
  server_id = azurerm_postgresql_flexible_server.target.id
  charset   = "UTF8"
  collation = "en_US.utf8"
}

resource "azurerm_postgresql_flexible_server_firewall_rule" "operator" {
  name             = "operator"
  server_id        = azurerm_postgresql_flexible_server.target.id
  start_ip_address = var.operator_ip
  end_ip_address   = var.operator_ip
}

resource "azurerm_consumption_budget_resource_group" "lab" {
  count = var.budget_alert_email == null ? 0 : 1

  name              = "budget-${local.name}"
  resource_group_id = azurerm_resource_group.lab.id
  amount            = var.budget_amount
  time_grain        = "Monthly"

  time_period {
    start_date = formatdate("YYYY-MM-01'T'00:00:00Z", plantimestamp())
  }

  notification {
    enabled        = true
    threshold      = 1
    threshold_type = "Actual"
    operator       = "GreaterThan"
    contact_emails = [var.budget_alert_email]
  }

  lifecycle {
    ignore_changes = [time_period]
  }
}
