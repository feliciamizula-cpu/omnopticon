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

output "argus_web_ip" {
  description = "External IP of the argus-web LoadBalancer (empty until GCP assigns it)."
  value       = var.apply_aspirate_manifests ? try(kubernetes_service.argus_web_lb[0].status[0].load_balancer[0].ingress[0].ip, "") : ""
}

output "aspire_dashboard_ip" {
  description = "External IP of the Aspire dashboard LoadBalancer (empty until GCP assigns it)."
  value       = var.apply_aspirate_manifests ? try(kubernetes_service.aspire_dashboard_lb[0].status[0].load_balancer[0].ingress[0].ip, "") : ""
}
