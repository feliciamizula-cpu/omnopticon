resource "google_project_service" "container" {
  service            = "container.googleapis.com"
  disable_on_destroy = false
}

resource "google_project_service" "artifact_registry" {
  service            = "artifactregistry.googleapis.com"
  disable_on_destroy = false
}

resource "google_artifact_registry_repository" "argus" {
  location      = var.region
  repository_id = var.artifact_registry_repository
  description   = "Argus application container images"
  format        = "DOCKER"

  depends_on = [google_project_service.artifact_registry]
}

resource "google_service_account" "gke_nodes" {
  account_id   = "argus-gke-nodes"
  display_name = "Argus GKE node pool"
}

resource "google_project_iam_member" "nodes_artifact_reader" {
  project = var.project_id
  role    = "roles/artifactregistry.reader"
  member  = "serviceAccount:${google_service_account.gke_nodes.email}"
}

resource "google_container_cluster" "argus" {
  provider = google-beta

  name     = var.cluster_name
  location = var.region

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

  node_config {
    disk_size_gb = 20
    disk_type    = "pd-standard"
  }

  addons_config {
    horizontal_pod_autoscaling {
      disabled = false
    }
    http_load_balancing {
      disabled = false
    }
  }

  depends_on = [google_project_service.container]
}

resource "google_container_node_pool" "primary" {
  provider = google-beta

  name     = "argus-primary"
  location = var.region
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
    machine_type    = var.machine_type
    disk_size_gb    = 30
    disk_type       = "pd-standard"
    service_account = google_service_account.gke_nodes.email
    oauth_scopes    = ["https://www.googleapis.com/auth/cloud-platform"]

    labels = local.common_labels
  }
}

resource "kubernetes_namespace" "argus" {
  metadata {
    name   = var.namespace
    labels = local.common_labels
  }

  depends_on = [google_container_node_pool.primary]
}

resource "helm_release" "keda" {
  count = var.install_keda ? 1 : 0

  name             = "keda"
  repository       = "https://kedacore.github.io/charts"
  chart            = "keda"
  namespace        = "keda"
  create_namespace = true

  depends_on = [google_container_node_pool.primary]
}

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
    helm_release.keda
  ]
}

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

resource "google_project_iam_member" "github_actions_service_usage_consumer" {
  count = var.github_actions_service_account != "" ? 1 : 0

  project = var.project_id
  role    = "roles/serviceusage.serviceUsageConsumer"
  member  = "serviceAccount:${var.github_actions_service_account}"
}

resource "google_project_iam_member" "github_actions_service_account_admin" {
  count = var.github_actions_service_account != "" ? 1 : 0

  project = var.project_id
  role    = "roles/iam.serviceAccountAdmin"
  member  = "serviceAccount:${var.github_actions_service_account}"
}

resource "google_project_iam_member" "github_actions_compute_admin" {
  count = var.github_actions_service_account != "" ? 1 : 0

  project = var.project_id
  role    = "roles/compute.admin"
  member  = "serviceAccount:${var.github_actions_service_account}"
}

resource "google_project_iam_member" "github_actions_container_admin" {
  count = var.github_actions_service_account != "" ? 1 : 0

  project = var.project_id
  role    = "roles/container.admin"
  member  = "serviceAccount:${var.github_actions_service_account}"
}
