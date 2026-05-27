variable "project_id" {
  description = "GCP project that owns the GKE cluster and Artifact Registry repository."
  type        = string
}

variable "region" {
  description = "GCP region for regional resources."
  type        = string
  default     = "us-central1"
}

variable "cluster_name" {
  description = "GKE cluster name."
  type        = string
  default     = "argus-gke"
}

variable "namespace" {
  description = "Kubernetes namespace for Argus workloads."
  type        = string
  default     = "argus"
}

variable "artifact_registry_repository" {
  description = "Artifact Registry Docker repository for Argus images."
  type        = string
  default     = "argus"
}

# ── Core node pool (stateful services, databases, API gateway, web) ──────────

variable "core_machine_type" {
  description = "Machine type for the core-services node pool."
  type        = string
  default     = "e2-standard-2"
}

variable "core_disk_size_gb" {
  description = "Boot disk size in GB for core service nodes (pd-standard HDD)."
  type        = number
  default     = 30
}

variable "core_min_node_count" {
  description = "Minimum core nodes."
  type        = number
  default     = 2
}

variable "core_max_node_count" {
  description = "Maximum core nodes. Kept small to fit a 32-vCPU regional quota; raise after requesting a quota increase."
  type        = number
  default     = 2
}

# ── Worker node pool (autoscaling, spot/preemptible) ──────────────────────────

variable "worker_machine_type" {
  description = "Machine type for the worker autoscaling node pool."
  type        = string
  default     = "e2-standard-2"
}

variable "worker_spot" {
  description = "Use Spot VMs for the worker pool (cheaper, may be reclaimed)."
  type        = bool
  default     = true
}

variable "min_node_count" {
  description = "Minimum worker nodes (can scale to zero when idle)."
  type        = number
  default     = 0
}

variable "max_node_count" {
  description = "Maximum worker nodes. Kept small to fit a 32-vCPU regional quota; raise after requesting a quota increase."
  type        = number
  default     = 3
}

# ── Workload support resources ────────────────────────────────────────────────
# Controls whether to create the eventbus secret, Aspire dashboard, and public
# LoadBalancer services. Set to true only in the deploy-full job (not provision).

variable "apply_workload_resources" {
  description = "When true, create the eventbus secret, Aspire dashboard, and public LoadBalancer services."
  type        = bool
  default     = false
}

# ── Worker scaling (KEDA queue-depth driven) ──────────────────────────────────

variable "create_worker_keda_scalers" {
  description = "Create KEDA ScaledObjects for continuous workers driven by RabbitMQ queue depth."
  type        = bool
  default     = true
}

variable "worker_min_replicas" {
  description = "Minimum replicas per continuous worker (0 = scale-to-zero)."
  type        = number
  default     = 0
}

variable "worker_max_replicas" {
  description = "Maximum replicas per continuous worker."
  type        = number
  default     = 10
}

variable "worker_queue_threshold" {
  description = "Queue length threshold per replica; KEDA adds a replica per N messages."
  type        = number
  default     = 5
}

variable "worker_cooldown_seconds" {
  description = "Idle seconds before KEDA scales a worker back to min replicas."
  type        = number
  default     = 300
}

variable "install_keda" {
  description = "Install KEDA so queue/custom metric scaled workers can be added without changing cluster provisioning."
  type        = bool
  default     = true
}

# ── Aspire dashboard ──────────────────────────────────────────────────────────

variable "dashboard_browser_token" {
  description = "Browser token required to sign in to the Aspire dashboard. If empty, the dashboard is unsecured."
  type        = string
  default     = ""
  sensitive   = true
}

# ── Backend secrets (substituted into manifests by patch-secrets.py too) ─────

variable "eventbus_password" {
  description = "RabbitMQ default user password. Used by KEDA's rabbitmq trigger auth."
  type        = string
  default     = ""
  sensitive   = true
}

variable "eventbus_amqp_uri" {
  description = "Full RabbitMQ AMQP URI (with URL-encoded password) for KEDA TriggerAuthentication."
  type        = string
  default     = ""
  sensitive   = true
}
