resource "kubernetes_namespace" "argus" {
  metadata {
    name = "argus"
  }
}