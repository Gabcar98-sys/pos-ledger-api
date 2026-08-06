resource "aws_db_subnet_group" "main" {
  name       = local.name
  subnet_ids = aws_subnet.private[*].id
}

resource "random_password" "db" {
  length  = 32
  special = false # Postgres URLs carry this password; percent-encoding it is a bug factory
}

resource "aws_db_parameter_group" "main" {
  name   = local.name
  family = "postgres16"

  # Every statement slower than a second is logged with its plan-relevant detail. This is
  # the setting that turns "the report got slow at some point" into a timestamped list —
  # the same kind of evidence docs/query-optimization.md is built from.
  # Postgres refuses any connection that is not over TLS. The application already asks
  # for sslmode=require; this is the half that does not depend on the client asking.
  parameter {
    name  = "rds.force_ssl"
    value = "1"
  }

  parameter {
    name  = "log_min_duration_statement"
    value = "1000"
  }

  parameter {
    name  = "log_statement"
    value = "ddl"
  }

  parameter {
    name  = "log_connections"
    value = "1"
  }

  parameter {
    name  = "log_disconnections"
    value = "1"
  }

  # Deadlocks are the failure mode this schema is most exposed to, given that sales take
  # row locks. If one ever happens, the log has to say which two statements it was.
  parameter {
    name  = "log_lock_waits"
    value = "1"
  }
}

resource "aws_iam_role" "rds_monitoring" {
  name = "${local.name}-rds-monitoring"
  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [{
      Effect    = "Allow"
      Principal = { Service = "monitoring.rds.amazonaws.com" }
      Action    = "sts:AssumeRole"
    }]
  })
}

resource "aws_iam_role_policy_attachment" "rds_monitoring" {
  role       = aws_iam_role.rds_monitoring.name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AmazonRDSEnhancedMonitoringRole"
}

resource "aws_db_instance" "main" {
  # checkov:skip=CKV_AWS_157: Multi-AZ is a cost decision, not a technical one. This is a
  # demo with a 7-day backup window; production flips multi_az and doubles the bill.
  identifier     = local.name
  engine         = "postgres"
  engine_version = "16"

  instance_class    = var.db_instance_class
  allocated_storage = var.db_allocated_storage
  storage_type      = "gp3"

  db_name  = "posledger"
  username = "posledger"
  password = random_password.db.result

  db_subnet_group_name   = aws_db_subnet_group.main.name
  vpc_security_group_ids = [aws_security_group.database.id]
  publicly_accessible    = false

  storage_encrypted = true
  kms_key_id        = aws_kms_key.logs.arn

  # Single AZ for a demo. Production flips this and doubles the bill; it is the one
  # setting here that is a business decision rather than a technical one.
  multi_az = false

  backup_retention_period = 7
  backup_window           = "05:00-06:00"
  maintenance_window      = "sun:06:00-sun:07:00"

  auto_minor_version_upgrade = true
  deletion_protection        = true
  skip_final_snapshot        = false
  final_snapshot_identifier  = "${local.name}-final"

  parameter_group_name  = aws_db_parameter_group.main.name
  copy_tags_to_snapshot = true

  performance_insights_enabled    = true
  performance_insights_kms_key_id = aws_kms_key.logs.arn
  enabled_cloudwatch_logs_exports = ["postgresql", "upgrade"]

  monitoring_interval = 60
  monitoring_role_arn = aws_iam_role.rds_monitoring.arn

  # IAM auth is on so an operator can connect without a shared password. The
  # application still uses the password, because a token that expires every 15
  # minutes is the wrong tool for a connection pool.
  iam_database_authentication_enabled = true
}

# The application never sees the password, only this secret's ARN, and only the task
# role can read it. Rotating the password is then a database change plus a secret
# update, with no deployment involved.
resource "aws_secretsmanager_secret" "database_url" {
  # checkov:skip=CKV2_AWS_57: automatic rotation needs a rotation Lambda that can change
  # the RDS password and update this secret in the right order. It is the next piece of
  # work on this stack, not something to claim by ticking a box — see infra/README.md.
  name                    = "${local.name}/database-url"
  kms_key_id              = aws_kms_key.logs.arn
  recovery_window_in_days = 7
}

resource "aws_secretsmanager_secret_version" "database_url" {
  secret_id = aws_secretsmanager_secret.database_url.id
  secret_string = format(
    "postgres://%s:%s@%s/%s?sslmode=require",
    aws_db_instance.main.username,
    random_password.db.result,
    aws_db_instance.main.endpoint,
    aws_db_instance.main.db_name
  )
}

resource "random_password" "jwt" {
  length  = 64
  special = false
}

resource "aws_secretsmanager_secret" "jwt" {
  # checkov:skip=CKV2_AWS_57: see the note on the database secret above.
  name                    = "${local.name}/jwt-secret"
  kms_key_id              = aws_kms_key.logs.arn
  recovery_window_in_days = 7
}

resource "aws_secretsmanager_secret_version" "jwt" {
  secret_id     = aws_secretsmanager_secret.jwt.id
  secret_string = random_password.jwt.result
}
