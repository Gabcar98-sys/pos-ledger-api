output "api_url" {
  description = "Public endpoint. Point a CNAME at this."
  value       = "https://${aws_lb.main.dns_name}"
}

output "database_endpoint" {
  description = "RDS endpoint. Reachable only from inside the VPC."
  value       = aws_db_instance.main.endpoint
}

output "database_secret_arn" {
  description = "Secrets Manager ARN holding the connection URL."
  value       = aws_secretsmanager_secret.database_url.arn
}

output "github_deploy_role_arn" {
  description = "Set this as the role-to-assume in the deploy workflow. No AWS keys are ever stored in GitHub."
  value       = aws_iam_role.github_deploy.arn
}

output "ecs_cluster" {
  value = aws_ecs_cluster.main.name
}

output "ecs_service" {
  value = aws_ecs_service.api.name
}
