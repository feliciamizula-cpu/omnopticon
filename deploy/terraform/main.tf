# ── Artifact Registry ─────────────────────────────────────────────────────────

resource "google_artifact_registry_repository" "argus" {
  location      = var.region
  repository_id = var.artifact_registry_repository
  description   = "Argus application container images"
  format        = "DOCKER"
}

# ── GKE cluster ───────────────────────────────────────────────────────────────

resource "google_container_cluster" "argus" {
  provider = google-beta

  name     = var.cluster_name
  location = "${var.region}-a"

  deletion_protection      = false
  remove_default_node_pool = true
  initial_node_count       = 1

  workload_identity_config {
    workload_pool = "${var.project_id}.svc.id.goog"
  }

  release_channel {
    channel = "REGULAR"
  }

  ip_allocation_policy {}

  addons_config {
    horizontal_pod_autoscaling {
      disabled = false
    }
    http_load_balancing {
      disabled = false
    }
  }

  lifecycle {
    ignore_changes = [node_config]
  }
}

# ── Core node pool: autoscaling 1-3 × e2-standard-2 ──────────────────────────
# Runs: services, databases (postgres/redis/rabbitmq), API gateway, web app,
# Aspire dashboard. Stays on regular (non-spot) VMs to keep stateful workloads
# from being preempted.

resource "google_container_node_pool" "core" {
  provider = google-beta

  name     = "argus-core"
  location = "${var.region}-a"
  cluster  = google_container_cluster.argus.name

  autoscaling {
    min_node_count = var.core_min_node_count
    max_node_count = var.core_max_node_count
  }

  management {
    auto_repair  = true
    auto_upgrade = true
  }

  node_config {
    machine_type = var.core_machine_type
    disk_size_gb = var.core_disk_size_gb
    # pd-standard (HDD) keeps us under the regional SSD_TOTAL_GB quota.
    # Boot time is a few minutes longer; irrelevant for long-running nodes.
    disk_type    = "pd-standard"
    oauth_scopes = ["https://www.googleapis.com/auth/cloud-platform"]

    labels = merge(local.common_labels, {
      argus-nodepool = "core"
    })
  }
}

# ── Worker node pool: spot 0-5 × e2-standard-2 ───────────────────────────────
# Runs: all background workers. Scales to zero when no queues have backlog.

resource "google_container_node_pool" "workers" {
  provider = google-beta

  name     = "argus-workers"
  location = "${var.region}-a"
  cluster  = google_container_cluster.argus.name

  autoscaling {
    min_node_count = var.min_node_count
    max_node_count = var.max_node_count
  }

  management {
    auto_repair  = true
    auto_upgrade = true
  }

  node_config {
    machine_type = var.worker_machine_type
    disk_size_gb = 30
    disk_type    = "pd-standard"
    spot         = var.worker_spot
    oauth_scopes = ["https://www.googleapis.com/auth/cloud-platform"]

    labels = merge(local.common_labels, {
      argus-nodepool = "workers"
    })

    # Workloads must opt-in via tolerations + nodeSelector. Prevents core
    # services from landing on spot nodes by accident.
    taint {
      key    = "argus/workload"
      value  = "worker"
      effect = "NO_SCHEDULE"
    }
  }
}

# ── Namespace ─────────────────────────────────────────────────────────────────

resource "kubernetes_namespace" "argus" {
  metadata {
    name   = var.namespace
    labels = local.common_labels
  }

  depends_on = [
    google_container_node_pool.core,
    google_container_node_pool.workers,
  ]
}

# ── Vertical Pod Autoscaler (VPA) ───────────────────────────────────────

resource "helm_release" "vpa" {
  count = var.install_vpa ? 1 : 0
  name = "vpa"
  repository = "https://charts.fairwinds.com/stable"
  chart = "vpa"
  namespace = "vpa"
  create_namespace = true

  depends_on = [
    google_container_node_pool.core,
    google_container_node_pool.workers,
  ]
}

# ── KEDA ──────────────────────────────────────────────────────────────────────

resource "helm_release" "keda" {
  count = var.install_keda ? 1 : 0

  name = "keda"
  repository = "https://kedacore.github.io/charts"
  chart = "keda"
  namespace = "keda"
  create_namespace = true

  depends_on = [
    google_container_node_pool.core,
    google_container_node_pool.workers,
  ]
}

# ── Static external IPs for the two public LoadBalancers ─────────────────────
# These survive Terraform churn and let us bookmark stable URLs.

resource "google_compute_address" "web" {
  name         = "argus-web-ip"
  region       = var.region
  address_type = "EXTERNAL"
}

resource "google_compute_address" "dashboard" {
  name         = "argus-dashboard-ip"
  region       = var.region
  address_type = "EXTERNAL"
}

# ── Eventbus / Postgres / Redis secrets ──────────────────────────────────────
# Aspirate is invoked with --disable-secrets in CI; passwords are substituted
# into manifests by deploy/patch-secrets.py. We also create K8s Secrets that
# KEDA can reference for AMQP auth.

resource "kubernetes_secret" "eventbus" {
  count = var.apply_workload_resources ? 1 : 0

  metadata {
    name      = "argus-eventbus"
    namespace = var.namespace
    labels    = local.common_labels
  }

  data = {
    host     = "eventbus"
    port     = "5672"
    username = "guest"
    password = var.eventbus_password
    amqp_uri = var.eventbus_amqp_uri
  }

  type = "Opaque"

  depends_on = [kubernetes_namespace.argus]
}

# ── KEDA TriggerAuthentication for RabbitMQ ──────────────────────────────────

resource "kubectl_manifest" "keda_rabbitmq_auth" {
  count = var.apply_workload_resources && var.create_worker_keda_scalers ? 1 : 0

  yaml_body = yamlencode({
    apiVersion = "keda.sh/v1alpha1"
    kind       = "TriggerAuthentication"
    metadata = {
      name      = "argus-rabbitmq-auth"
      namespace = var.namespace
    }
    spec = {
      secretTargetRef = [
        {
          parameter = "host"
          name      = "argus-eventbus"
          key       = "amqp_uri"
        }
      ]
    }
  })

  depends_on = [
    helm_release.keda,
    kubernetes_secret.eventbus,
  ]
}

# ── Vertical Pod Autoscaler (VPA) for queue-driven workers ────────────────

resource "kubectl_manifest" "vpa_workers" {
  for_each = var.apply_workload_resources && var.create_vpa_for_workers ? local.worker_queues : {}

  yaml_body = yamlencode({
    apiVersion = "autoscaling.k8s.io/v1"
    kind = "VerticalPodAutoscaler"
    metadata = {
      name = "${each.key}-vpa"
      namespace = var.namespace
    }
    spec = {
      targetRef = {
        apiVersion = "apps/v1"
        kind = "Deployment"
        name = each.key
      }
      updatePolicy = {
        updateMode = "Auto"
      }
    }
  })

  depends_on = [
    kubernetes_namespace.argus,
    helm_release.keda,
  ]
}

# ── KEDA ScaledObjects: one per queue-driven worker ──────────────────────────

resource "kubectl_manifest" "keda_worker_scalers" {
  for_each = var.apply_workload_resources && var.create_worker_keda_scalers ? local.worker_queues : {}
    apiVersion = "keda.sh/v1alpha1"
    kind       = "ScaledObject"
    metadata = {
      name      = "${each.key}-scaler"
      namespace = var.namespace
      labels    = local.common_labels
    }
    spec = {
      scaleTargetRef = {
        name = each.key
      }
      minReplicaCount  = var.worker_min_replicas
      maxReplicaCount  = var.worker_max_replicas
      pollingInterval  = 15
      cooldownPeriod   = var.worker_cooldown_seconds
      triggers = [
        {
          type = "rabbitmq"
          metadata = {
            protocol    = "amqp"
            queueName   = each.value
            mode        = "QueueLength"
            value       = tostring(var.worker_queue_threshold)
          }
          authenticationRef = {
            name = "argus-rabbitmq-auth"
          }
        }
      ]
    }
  })

  depends_on = [
    kubectl_manifest.keda_rabbitmq_auth,
  ]
}

# ── Public LoadBalancer: Argus web app ────────────────────────────────────────

resource "kubernetes_service" "argus_web_lb" {
  count = var.apply_workload_resources ? 1 : 0

  metadata {
    name      = "argus-web-public"
    namespace = var.namespace
    labels    = local.common_labels
  }

  spec {
    type             = "LoadBalancer"
    load_balancer_ip = google_compute_address.web.address

    selector = {
      app = "argus-web"
    }

    port {
      name        = "http"
      port        = 80
      target_port = 8082
    }
  }

  depends_on = [kubernetes_namespace.argus]
}

# ── Aspire dashboard ──────────────────────────────────────────────────────────

resource "kubernetes_deployment" "aspire_dashboard" {
  count = var.apply_workload_resources ? 1 : 0

  metadata {
    name      = "aspire-dashboard"
    namespace = var.namespace
    labels    = merge(local.common_labels, { app = "aspire-dashboard" })
  }

  spec {
    replicas = 1

    selector {
      match_labels = { app = "aspire-dashboard" }
    }

    template {
      metadata {
        labels = merge(local.common_labels, { app = "aspire-dashboard" })
      }

      spec {
        node_selector = {
          "cloud.google.com/gke-nodepool" = "argus-core"
        }

        container {
          name  = "aspire-dashboard"
          image = "mcr.microsoft.com/dotnet/aspire-dashboard:9.0"

          port {
            name           = "ui"
            container_port = 18888
          }

          port {
            name           = "otlp-grpc"
            container_port = 18889
          }

          # Frontend token auth when a token is provided; otherwise unsecured.
          dynamic "env" {
            for_each = var.dashboard_browser_token == "" ? [1] : []
            content {
              name  = "DOTNET_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS"
              value = "true"
            }
          }

          dynamic "env" {
            for_each = var.dashboard_browser_token == "" ? [] : [1]
            content {
              name  = "Dashboard__Frontend__AuthMode"
              value = "BrowserToken"
            }
          }

          dynamic "env" {
            for_each = var.dashboard_browser_token == "" ? [] : [1]
            content {
              name  = "Dashboard__Frontend__BrowserToken"
              value = var.dashboard_browser_token
            }
          }

          # OTLP receiver auth: allow services in-cluster to send telemetry
          # without a key (they're on the trusted network).
          env {
            name  = "Dashboard__Otlp__AuthMode"
            value = "Unsecured"
          }

          env {
            name  = "ASPNETCORE_URLS"
            value = "http://+:18888"
          }

          env {
            name  = "DOTNET_DASHBOARD_OTLP_ENDPOINT_URL"
            value = "http://+:18889"
          }

          resources {
            requests = {
              cpu    = "100m"
              memory = "256Mi"
            }
            limits = {
              cpu    = "500m"
              memory = "512Mi"
            }
          }
        }
      }
    }
  }

  depends_on = [kubernetes_namespace.argus]
}

resource "kubernetes_service" "aspire_dashboard_lb" {
  count = var.apply_workload_resources ? 1 : 0

  metadata {
    name      = "aspire-dashboard-public"
    namespace = var.namespace
    labels    = local.common_labels
  }

  spec {
    type             = "LoadBalancer"
    load_balancer_ip = google_compute_address.dashboard.address

    selector = {
      app = "aspire-dashboard"
    }

    port {
      name        = "ui"
      port        = 80
      target_port = 18888
    }
  }

  depends_on = [kubernetes_deployment.aspire_dashboard]
}

# In-cluster Service so app pods can send OTLP without going through the LB.
resource "kubernetes_service" "aspire_dashboard_otlp" {
  count = var.apply_workload_resources ? 1 : 0

  metadata {
    name      = "aspire-dashboard-otlp"
    namespace = var.namespace
    labels    = local.common_labels
  }

  spec {
    type = "ClusterIP"

    selector = {
      app = "aspire-dashboard"
    }

    port {
      name        = "otlp-grpc"
      port        = 4317
      target_port = 18889
    }
  }

  depends_on = [kubernetes_deployment.aspire_dashboard]
}
