# Renames the old single node pool to the workers pool in Terraform state.
# The core pool (argus-core) is new and will be created fresh.
moved {
  from = google_container_node_pool.primary
  to   = google_container_node_pool.workers
}
