terraform {
  backend "gcs" {
    bucket = "project-30b3b95e-ed2b-4573-98a-terraform-state"
    prefix = "gke/deploy"
  }
}