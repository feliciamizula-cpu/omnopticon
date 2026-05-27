# Renames the old single node pool to the workers pool in Terraform state.
# The core pool (argus-core) is new and will be created fresh.
moved {
  from = google_container_node_pool.primary
  to   = google_container_node_pool.workers
}

# ── Removed resources ─────────────────────────────────────────────────────────
# These resources already exist in GCP and are managed out-of-band.
# destroy = false removes them from Terraform state without calling the GCP
# delete API (avoids needing Service Usage Admin / IAM Admin permissions in CI).

removed {
  from = google_project_service.container
  lifecycle { destroy = false }
}

removed {
  from = google_project_service.artifact_registry
  lifecycle { destroy = false }
}

removed {
  from = google_project_service.cloudresourcemanager
  lifecycle { destroy = false }
}

removed {
  from = google_project_service.iam
  lifecycle { destroy = false }
}

removed {
  from = google_project_service.compute
  lifecycle { destroy = false }
}

removed {
  from = google_service_account.gke_nodes
  lifecycle { destroy = false }
}

removed {
  from = google_project_iam_member.nodes_artifact_reader
  lifecycle { destroy = false }
}

removed {
  from = google_project_iam_member.github_actions_service_usage_consumer
  lifecycle { destroy = false }
}

removed {
  from = google_project_iam_member.github_actions_service_account_admin
  lifecycle { destroy = false }
}

removed {
  from = google_project_iam_member.github_actions_compute_admin
  lifecycle { destroy = false }
}

removed {
  from = google_project_iam_member.github_actions_container_admin
  lifecycle { destroy = false }
}
