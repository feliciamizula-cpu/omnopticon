output "cluster_name" {
  value = google_container_cluster.argus.name
}

output "cluster_location" {
  value = google_container_cluster.argus.location
}

output "namespace" {
  value = kubernetes_namespace.argus.metadata[0].name
}

output "artifact_registry_repository" {
  value = "${var.region}-docker.pkg.dev/${var.project_id}/${google_artifact_registry_repository.argus.repository_id}"
}

output "get_credentials_command" {
  value = "gcloud container clusters get-credentials ${google_container_cluster.argus.name} --region ${google_container_cluster.argus.location} --project ${var.project_id}"
}
