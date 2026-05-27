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

# ── Core node pool (n2-standard-16, 1 TB SSD, fixed size) ────────────────────
# Runs: services, databases (postgres/redis/rabbitmq), API gateway, web app

resource "google_container_node_pool" "core" {
  provider = google-beta

  name     = "argus-core"
  location = "${var.region}-a"
  cluster  = google_container_cluster.argus.name

  node_count = 1

  management {
    auto_repair  = true
    auto_upgrade = true
  }

  node_config {
    machine_type = var.core_machine_type
    disk_size_gb = var.core_disk_size_gb
    disk_type    = "pd-ssd"
    oauth_scopes = ["https://www.googleapis.com/auth/cloud-platform"]

    labels = merge(local.common_labels, {
      argus-nodepool = "core"
    })
  }
}

# ── Worker node pool (e2-standard-4, autoscaling 0–10) ───────────────────────
# Runs: all background workers (continuous, ephemeral, validation)

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
    disk_size_gb = 50
    disk_type    = "pd-standard"
    oauth_scopes = ["https://www.googleapis.com/auth/cloud-platform"]

    labels = merge(local.common_labels, {
      argus-nodepool = "workers"
    })
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

# ── KEDA ──────────────────────────────────────────────────────────────────────

resource "helm_release" "keda" {
  count = var.install_keda ? 1 : 0

  name             = "keda"
  repository       = "https://kedacore.github.io/charts"
  chart            = "keda"
  namespace        = "keda"
  create_namespace = true

  depends_on = [
    google_container_node_pool.core,
    google_container_node_pool.workers,
  ]
}

# ── Aspirate-generated workload manifests ─────────────────────────────────────

data "kubectl_file_documents" "aspirate" {
  count = var.apply_aspirate_manifests ? 1 : 0

  content = join("\n---\n", [
    for file_name in fileset(var.aspirate_manifest_dir, "**/*.yaml") :
    file("${var.aspirate_manifest_dir}/${file_name}")
    if !endswith(file_name, "kustomization.yaml") && !endswith(file_name, "kustomization.yml")
  ])
}

resource "kubectl_manifest" "aspirate" {
  for_each = var.apply_aspirate_manifests ? data.kubectl_file_documents.aspirate[0].manifests : {}

  yaml_body          = each.value
  override_namespace = var.namespace

  depends_on = [
    kubernetes_namespace.argus,
    helm_release.keda,
  ]
}

# ── Continuous-worker HPAs ────────────────────────────────────────────────────

resource "kubernetes_horizontal_pod_autoscaler_v2" "continuous_workers" {
  for_each = var.apply_aspirate_manifests && var.create_worker_hpas ? local.continuous_workers : toset([])

  metadata {
    name      = each.key
    namespace = var.namespace
    labels    = local.common_labels
  }

  spec {
    min_replicas = var.worker_min_replicas
    max_replicas = var.worker_max_replicas

    scale_target_ref {
      api_version = "apps/v1"
      kind        = "Deployment"
      name        = each.key
    }

    metric {
      type = "Resource"
      resource {
        name = "cpu"
        target {
          type                = "Utilization"
          average_utilization = var.worker_cpu_utilization
        }
      }
    }
  }

  depends_on = [kubectl_manifest.aspirate]
}

# ── Public LoadBalancer: Argus web app ────────────────────────────────────────

resource "kubernetes_service" "argus_web_lb" {
  count = var.apply_aspirate_manifests ? 1 : 0

  metadata {
    name      = "argus-web-public"
    namespace = var.namespace
    labels    = local.common_labels
  }

  spec {
    type = "LoadBalancer"

    selector = {
      app = "argus-web"
    }

    port {
      name        = "http"
      port        = 80
      target_port = 8082
    }
  }

  depends_on = [kubectl_manifest.aspirate]
}

# ── Aspire dashboard deployment + public LoadBalancer ─────────────────────────

resource "kubernetes_deployment" "aspire_dashboard" {
  count = var.apply_aspirate_manifests ? 1 : 0

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
            container_port = 18080
          }

          port {
            name           = "otlp-grpc"
            container_port = 18888
          }

          port {
            name           = "otlp-http"
            container_port = 18889
          }

          env {
            name  = "DOTNET_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS"
            value = "true"
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
  count = var.apply_aspirate_manifests ? 1 : 0

  metadata {
    name      = "aspire-dashboard-public"
    namespace = var.namespace
    labels    = local.common_labels
  }

  spec {
    type = "LoadBalancer"

    selector = {
      app = "aspire-dashboard"
    }

    port {
      name        = "ui"
      port        = 80
      target_port = 18080
    }
  }

  depends_on = [kubernetes_deployment.aspire_dashboard]
}

