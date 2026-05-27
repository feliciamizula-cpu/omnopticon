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

# Static external IPs reserved by Terraform — survive cluster rebuilds.
output "argus_web_ip" {
  description = "Static external IP for the web app LoadBalancer."
  value       = google_compute_address.web.address
}

output "aspire_dashboard_ip" {
  description = "Static external IP for the Aspire dashboard LoadBalancer."
  value       = google_compute_address.dashboard.address
}

output "argus_web_url" {
  description = "Public URL of the Argus web app."
  value       = "http://${google_compute_address.web.address}"
}

output "aspire_dashboard_url" {
  description = "Public URL of the Aspire dashboard."
  value       = "http://${google_compute_address.dashboard.address}"
}
