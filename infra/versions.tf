terraform {
  required_version = ">= 1.6"

  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = "~> 5.60"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.6"
    }
  }

  # Left unconfigured on purpose. A backend block hardcoding someone else's bucket is
  # noise in a repository; the bucket and lock table are passed with -backend-config.
  # backend "s3" {}
}

provider "aws" {
  region = var.region

  # Off for a real apply, on in CI. With these three set, `terraform plan` renders the
  # entire resource graph without calling STS, which is what lets every pull request be
  # checked against a configuration that has never been pointed at an account.
  # The configuration deliberately uses no data sources that read live AWS state, for
  # the same reason.
  skip_credentials_validation = var.offline_plan
  skip_requesting_account_id  = var.offline_plan
  skip_metadata_api_check     = var.offline_plan

  default_tags {
    tags = {
      Project     = "pos-ledger-api"
      Environment = var.environment
      ManagedBy   = "terraform"
    }
  }
}
