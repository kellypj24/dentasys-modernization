# The Container Apps half of the cost guard. Mocked providers, as in free_tier.

mock_provider "azurerm" {}
mock_provider "random" {}

variables {
  operator_ip  = "203.0.113.10"
  expires_on   = "2026-11-07"
  api_image    = "ghcr.io/example/dentasys-api:test"
  worker_image = "ghcr.io/example/dentasys-worker:test"
}

run "database_only_without_images" {
  command = plan

  variables {
    api_image    = null
    worker_image = null
  }

  assert {
    condition = (
      length(azurerm_container_app_environment.lab) == 0 &&
      length(azurerm_container_app.api) == 0 &&
      length(azurerm_container_app_job.outbox) == 0 &&
      length(azurerm_container_app_job.recall) == 0 &&
      length(azurerm_postgresql_flexible_server_firewall_rule.azure_services) == 0
    )
    error_message = "No images given, so nothing but the database should be planned."
  }
}

run "api_scales_to_zero_and_admits_only_the_operator" {
  command = plan

  assert {
    condition     = azurerm_container_app.api[0].template[0].min_replicas == 0
    error_message = "An API with min_replicas > 0 bills around the clock."
  }
  assert {
    condition     = azurerm_container_app.api[0].template[0].max_replicas <= 1
    error_message = "More than one replica is not needed for a lab."
  }
  assert {
    condition     = azurerm_container_app.api[0].ingress[0].ip_security_restriction[0].ip_address_range == "203.0.113.10/32"
    error_message = "Ingress must admit only the operator, or anyone can wake the app."
  }
}

run "worker_is_a_scheduled_job_not_a_poller" {
  command = plan

  assert {
    condition     = length(azurerm_container_app_job.outbox[0].schedule_trigger_config) == 1
    error_message = "The worker must run on a schedule."
  }
  assert {
    condition     = azurerm_container_app_job.outbox[0].template[0].container[0].args == tolist(["--once"])
    error_message = "Without --once the job never exits and runs until the replica timeout."
  }
}

run "environment_has_no_log_workspace" {
  command = plan

  assert {
    condition     = azurerm_container_app_environment.lab[0].log_analytics_workspace_id == null
    error_message = "A Log Analytics workspace bills per GB ingested."
  }
}

run "recall_runs_once_a_day_and_exits" {
  command = plan

  assert {
    condition     = azurerm_container_app_job.recall[0].template[0].container[0].args == tolist(["--recall"])
    error_message = "The recall job must run the recall mode, which exits when done."
  }
  assert {
    condition     = length(split(" ", azurerm_container_app_job.recall[0].schedule_trigger_config[0].cron_expression)) == 5 && !startswith(azurerm_container_app_job.recall[0].schedule_trigger_config[0].cron_expression, "*")
    error_message = "The recall job must fire at a fixed minute, not every minute."
  }
}
