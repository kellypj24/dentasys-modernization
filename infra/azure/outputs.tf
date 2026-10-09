output "resource_group" {
  value = azurerm_resource_group.lab.name
}

output "pg_host" {
  value = azurerm_postgresql_flexible_server.target.fqdn
}

output "pg_user" {
  value = azurerm_postgresql_flexible_server.target.administrator_login
}

output "pg_password" {
  value     = random_password.pg_admin.result
  sensitive = true
}

output "api_url" {
  value = local.deploy_apps ? "https://${azurerm_container_app.api[0].ingress[0].fqdn}" : null
}
