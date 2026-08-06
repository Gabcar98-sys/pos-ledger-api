# This bucket holds the load balancer's access logs and nothing else.
resource "aws_s3_bucket" "logs" {
  # checkov:skip=CKV_AWS_144: cross-region replication of 90-day access logs is not worth
  # the storage and transfer cost for this workload.
  # checkov:skip=CKV2_AWS_62: nothing consumes these logs as events; they are read on demand.
  # checkov:skip=CKV_AWS_145: ALB log delivery cannot write with a customer managed key;
  # SSE-S3 is the strongest option the service supports here.
  # checkov:skip=CKV_AWS_18: access logging on the log bucket would write its own access
  # logs into a second bucket, which then needs a third. The chain has to stop somewhere.
  bucket        = "${local.name}-alb-logs"
  force_destroy = false
}

resource "aws_s3_bucket_public_access_block" "logs" {
  bucket                  = aws_s3_bucket.logs.id
  block_public_acls       = true
  block_public_policy     = true
  ignore_public_acls      = true
  restrict_public_buckets = true
}

resource "aws_s3_bucket_server_side_encryption_configuration" "logs" {
  bucket = aws_s3_bucket.logs.id

  rule {
    apply_server_side_encryption_by_default {
      # SSE-S3 rather than KMS: ALB log delivery cannot use a customer managed key.
      sse_algorithm = "AES256"
    }
  }
}

resource "aws_s3_bucket_versioning" "logs" {
  bucket = aws_s3_bucket.logs.id
  versioning_configuration { status = "Enabled" }
}

resource "aws_s3_bucket_lifecycle_configuration" "logs" {
  bucket = aws_s3_bucket.logs.id

  rule {
    id     = "expire"
    status = "Enabled"
    filter {}
    expiration { days = 90 }
    noncurrent_version_expiration { noncurrent_days = 30 }

    # Failed multipart uploads are invisible in the console and billed like any other
    # storage. Without this they accumulate forever.
    abort_incomplete_multipart_upload { days_after_initiation = 7 }
  }
}

data "aws_iam_policy_document" "logs" {
  statement {
    principals {
      type        = "Service"
      identifiers = ["logdelivery.elasticloadbalancing.amazonaws.com"]
    }
    actions   = ["s3:PutObject"]
    resources = ["${aws_s3_bucket.logs.arn}/*"]
  }
}

resource "aws_s3_bucket_policy" "logs" {
  bucket = aws_s3_bucket.logs.id
  policy = data.aws_iam_policy_document.logs.json
}

resource "aws_lb" "main" {
  # checkov:skip=CKV2_AWS_28: a WAF is the right answer for a public payments endpoint and
  # is left out here only because of cost (~$5/month plus per-rule and per-request charges).
  # It is the first thing to add if this stops being a demo — see infra/README.md.
  name               = local.name
  load_balancer_type = "application"
  internal           = false
  security_groups    = [aws_security_group.alb.id]
  subnets            = aws_subnet.public[*].id

  # Rejects requests with malformed headers instead of passing them through, which is
  # what makes request smuggling against the backend possible.
  drop_invalid_header_fields = true

  enable_deletion_protection = true
  idle_timeout               = 60

  access_logs {
    bucket  = aws_s3_bucket.logs.id
    prefix  = "alb"
    enabled = true
  }
}

resource "aws_lb_target_group" "api" {
  name        = local.name
  port        = var.container_port
  protocol    = "HTTP"
  target_type = "ip"
  vpc_id      = aws_vpc.main.id

  # Liveness, not readiness: a database blip should page someone, not make the load
  # balancer take every task out of service at the same moment.
  health_check {
    path                = "/health"
    matcher             = "200"
    interval            = 15
    timeout             = 5
    healthy_threshold   = 2
    unhealthy_threshold = 3
  }

  deregistration_delay = 30
}

resource "aws_lb_listener" "https" {
  load_balancer_arn = aws_lb.main.arn
  port              = 443
  protocol          = "HTTPS"
  ssl_policy        = "ELBSecurityPolicy-TLS13-1-2-2021-06"
  certificate_arn   = var.certificate_arn

  default_action {
    type             = "forward"
    target_group_arn = aws_lb_target_group.api.arn
  }
}

resource "aws_lb_listener" "http_redirect" {
  load_balancer_arn = aws_lb.main.arn
  port              = 80
  protocol          = "HTTP"

  default_action {
    type = "redirect"
    redirect {
      port        = "443"
      protocol    = "HTTPS"
      status_code = "HTTP_301"
    }
  }
}
