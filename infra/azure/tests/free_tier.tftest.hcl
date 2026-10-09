# Runs against mocked providers: no Azure account, no credentials, no cost.
# These assertions are the cost guard. A paid SKU fails `just check` before it
# can reach `terraform apply`.

mock_provider "azurerm" {}
mock_provider "random" {}

variables {
  operator_ip = "203.0.113.10"
  expires_on  = "2026-11-07"
}

run "postgres_stays_inside_the_free_allowance" {
  command = plan

  assert {
    condition     = azurerm_postgresql_flexible_server.target.sku_name == "B_Standard_B1ms"
    error_message = "Only B_Standard_B1ms is in the free allowance."
  }
  assert {
    condition     = azurerm_postgresql_flexible_server.target.storage_mb <= 32768
    error_message = "Storage above 32 GB is billed."
  }
  assert {
    condition     = azurerm_postgresql_flexible_server.target.auto_grow_enabled == false
    error_message = "Auto-grow can push storage past 32 GB without an apply."
  }
  assert {
    condition     = azurerm_postgresql_flexible_server.target.geo_redundant_backup_enabled == false
    error_message = "Geo-redundant backup is billed."
  }
  assert {
    condition     = length(azurerm_postgresql_flexible_server.target.high_availability) == 0
    error_message = "HA doubles the compute and is billed."
  }
}

run "every_billable_resource_carries_an_expiry" {
  command = plan

  assert {
    condition     = azurerm_resource_group.lab.tags["expires_on"] == "2026-11-07"
    error_message = "Resource group is missing expires_on."
  }
  assert {
    condition     = azurerm_postgresql_flexible_server.target.tags["expires_on"] == "2026-11-07"
    error_message = "Postgres server is missing expires_on."
  }
}

run "firewall_admits_one_address" {
  command = plan

  assert {
    condition = (
      azurerm_postgresql_flexible_server_firewall_rule.operator.start_ip_address ==
      azurerm_postgresql_flexible_server_firewall_rule.operator.end_ip_address
    )
    error_message = "Firewall rule must be a single address."
  }
}

run "budget_is_opt_in" {
  command = plan

  assert {
    condition     = length(azurerm_consumption_budget_resource_group.lab) == 0
    error_message = "No email given, so no budget should be created."
  }
}

run "rejects_an_ip_range" {
  command = plan

  variables {
    operator_ip = "0.0.0.0/0"
  }

  expect_failures = [var.operator_ip]
}
