variable "region" {
  description = "AWS region."
  type        = string
  default     = "us-east-1"
}

variable "environment" {
  description = "Environment name, used in resource names and tags."
  type        = string
  default     = "demo"
}

variable "name" {
  description = "Base name for every resource."
  type        = string
  default     = "pos-ledger"
}

# Written out rather than read from the aws_availability_zones data source so that
# `terraform plan` renders the full graph without calling AWS at all. That is what lets
# CI validate this configuration on every pull request without an account attached.
variable "availability_zones" {
  description = "Exactly two AZ suffixes; RDS and the ALB both need two subnets."
  type        = list(string)
  default     = ["a", "b"]

  validation {
    condition     = length(var.availability_zones) == 2
    error_message = "Provide exactly two availability zone suffixes."
  }
}

variable "vpc_cidr" {
  description = "CIDR block for the VPC."
  type        = string
  default     = "10.20.0.0/16"
}

variable "container_image" {
  description = "Image to run. Published by .github/workflows/ci.yml."
  type        = string
  default     = "ghcr.io/gabcar98-sys/pos-ledger-api:latest"
}

variable "container_port" {
  description = "Port the container listens on."
  type        = number
  default     = 8080
}

variable "desired_count" {
  description = "Number of Fargate tasks."
  type        = number
  default     = 2
}

variable "task_cpu" {
  description = "Fargate CPU units."
  type        = number
  default     = 512
}

variable "task_memory" {
  description = "Fargate memory in MiB."
  type        = number
  default     = 1024
}

variable "db_instance_class" {
  description = "RDS instance class."
  type        = string
  default     = "db.t4g.micro"
}

variable "db_allocated_storage" {
  description = "RDS storage in GB."
  type        = number
  default     = 20
}

variable "log_retention_days" {
  description = "CloudWatch log retention. Never leave logs on 'never expire'; it is a bill that only grows."
  type        = number
  default     = 30
}

variable "account_id" {
  description = <<-EOT
    AWS account id, used in the KMS key policy. Passed in rather than read from
    aws_caller_identity so that `terraform plan` needs no credentials; the placeholder
    default keeps CI working and must be replaced for a real apply.
  EOT
  type        = string
  default     = "000000000000"
}

variable "offline_plan" {
  description = "Set by CI so that `terraform plan` runs without AWS credentials. Never set for an apply."
  type        = bool
  default     = false
}

variable "certificate_arn" {
  description = <<-EOT
    ACM certificate for the HTTPS listener. There is no HTTP-only path: the API issues
    bearer tokens, and a token sent in clear is a token that has been given away.
    The default is a syntactically valid placeholder so that `terraform plan` renders in
    CI without an AWS account; a real apply must be given the real ARN.
  EOT
  type        = string
  default     = "arn:aws:acm:us-east-1:000000000000:certificate/00000000-0000-0000-0000-000000000000"
}

variable "github_repository" {
  description = "owner/repo allowed to assume the deploy role through OIDC."
  type        = string
  default     = "Gabcar98-sys/pos-ledger-api"
}
