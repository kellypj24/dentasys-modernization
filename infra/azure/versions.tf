terraform {
  required_version = ">= 1.7"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.0"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.6"
    }
  }
}

# subscription_id comes from ARM_SUBSCRIPTION_ID, which `just azure-up` sets
# from the logged-in az account.
provider "azurerm" {
  features {
    # Teardown must take everything, including resources Azure creates on its
    # own inside the group. Anything left behind is something that can bill.
    resource_group {
      prevent_deletion_if_contains_resources = false
    }
  }
}
