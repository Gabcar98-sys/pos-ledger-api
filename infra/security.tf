resource "aws_kms_key" "logs" {
  description             = "Encrypts CloudWatch log groups, RDS storage and secrets for ${local.name}"
  enable_key_rotation     = true
  deletion_window_in_days = 7
  policy                  = data.aws_iam_policy_document.kms_logs.json
}

# Not boilerplate: a KMS key with the default policy cannot be used by CloudWatch Logs at
# all, and the log group creation fails at apply time with an opaque AccessDenied. The
# service principal has to be granted use of the key, scoped by encryption context so it
# can only ever wrap keys for log groups in this account.
data "aws_iam_policy_document" "kms_logs" {
  # checkov:skip=CKV_AWS_109: this is the account root statement every KMS key policy must
  # carry. Without it the key has no administrator and cannot be modified or deleted by
  # anyone, including IAM administrators — AWS documents this as the required baseline.
  # checkov:skip=CKV_AWS_111: same statement; the principal is the account root, not a user.
  # checkov:skip=CKV_AWS_356: a key policy's resource is always "*" — it means "this key",
  # because the policy is attached to the key and can name nothing else.
  statement {
    sid       = "AllowAccountAdministration"
    actions   = ["kms:*"]
    resources = ["*"]

    principals {
      type        = "AWS"
      identifiers = ["arn:aws:iam::${var.account_id}:root"]
    }
  }

  statement {
    sid = "AllowCloudWatchLogs"

    actions = [
      "kms:Encrypt*",
      "kms:Decrypt*",
      "kms:ReEncrypt*",
      "kms:GenerateDataKey*",
      "kms:Describe*"
    ]

    resources = ["*"]

    principals {
      type        = "Service"
      identifiers = ["logs.${var.region}.amazonaws.com"]
    }

    condition {
      test     = "ArnLike"
      variable = "kms:EncryptionContext:aws:logs:arn"
      values   = ["arn:aws:logs:${var.region}:${var.account_id}:log-group:*"]
    }
  }
}

# The default security group of a fresh VPC allows all traffic between anything attached
# to it. Nothing here uses it — this resource exists to take its rules away, so that a
# future resource created without an explicit group is not silently wide open.
resource "aws_default_security_group" "locked" {
  vpc_id = aws_vpc.main.id
}

resource "aws_kms_alias" "logs" {
  name          = "alias/${local.name}-logs"
  target_key_id = aws_kms_key.logs.key_id
}

# ── Load balancer ──────────────────────────────────────────────────────────────
resource "aws_security_group" "alb" {
  name        = "${local.name}-alb"
  description = "Public entry point"
  vpc_id      = aws_vpc.main.id
}

resource "aws_vpc_security_group_ingress_rule" "alb_https" {
  security_group_id = aws_security_group.alb.id
  description       = "HTTPS from anywhere — this is the public endpoint"
  from_port         = 443
  to_port           = 443
  ip_protocol       = "tcp"
  cidr_ipv4         = "0.0.0.0/0"
}

resource "aws_vpc_security_group_ingress_rule" "alb_http_redirect" {
  # checkov:skip=CKV_AWS_260: port 80 is open because the listener on it does nothing but
  # issue a 301 to HTTPS. Closing it would not improve anything — it would just mean plain
  # HTTP requests time out instead of being redirected.
  security_group_id = aws_security_group.alb.id
  description       = "HTTP, only to be redirected to HTTPS"
  from_port         = 80
  to_port           = 80
  ip_protocol       = "tcp"
  cidr_ipv4         = "0.0.0.0/0"
}

# Egress is restricted to the tasks. A load balancer that can reach the whole internet
# is a data exfiltration path with no upside.
resource "aws_vpc_security_group_egress_rule" "alb_to_tasks" {
  security_group_id            = aws_security_group.alb.id
  description                  = "To the API tasks only"
  from_port                    = var.container_port
  to_port                      = var.container_port
  ip_protocol                  = "tcp"
  referenced_security_group_id = aws_security_group.tasks.id
}

# ── API tasks ──────────────────────────────────────────────────────────────────
resource "aws_security_group" "tasks" {
  name        = "${local.name}-tasks"
  description = "API tasks"
  vpc_id      = aws_vpc.main.id
}

resource "aws_vpc_security_group_ingress_rule" "tasks_from_alb" {
  security_group_id            = aws_security_group.tasks.id
  description                  = "Only the load balancer may reach the API"
  from_port                    = var.container_port
  to_port                      = var.container_port
  ip_protocol                  = "tcp"
  referenced_security_group_id = aws_security_group.alb.id
}

# The tasks need to pull the image from GHCR and reach Secrets Manager, both of which are
# HTTPS to addresses that are not known ahead of time. Pinning the destinations would mean
# VPC endpoints, and one of the two registries lives outside AWS entirely. Limiting egress
# to 443 is the narrowest rule that can actually be written here.
resource "aws_vpc_security_group_egress_rule" "tasks_https" {
  security_group_id = aws_security_group.tasks.id
  description       = "HTTPS out for the image pull and AWS APIs"
  from_port         = 443
  to_port           = 443
  ip_protocol       = "tcp"
  cidr_ipv4         = "0.0.0.0/0"
}

resource "aws_vpc_security_group_egress_rule" "tasks_to_db" {
  security_group_id            = aws_security_group.tasks.id
  description                  = "Postgres"
  from_port                    = 5432
  to_port                      = 5432
  ip_protocol                  = "tcp"
  referenced_security_group_id = aws_security_group.database.id
}

# ── Database ───────────────────────────────────────────────────────────────────
resource "aws_security_group" "database" {
  name        = "${local.name}-db"
  description = "Postgres, reachable only from the API tasks"
  vpc_id      = aws_vpc.main.id
}

resource "aws_vpc_security_group_ingress_rule" "db_from_tasks" {
  security_group_id            = aws_security_group.database.id
  description                  = "Only the API tasks may reach the database"
  from_port                    = 5432
  to_port                      = 5432
  ip_protocol                  = "tcp"
  referenced_security_group_id = aws_security_group.tasks.id
}
