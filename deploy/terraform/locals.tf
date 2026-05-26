locals {
  continuous_workers = toset([
    "amass-worker",
    "asset-scoring-worker",
    "dns-resolver-worker",
    "finding-deduper-worker",
    "fingerprint-worker",
    "headless-spider-worker",
    "html-dom-spider-worker",
    "http-probe-worker",
    "js-extractor-worker",
    "regex-scanner-worker",
    "subfinder-worker",
    "wordlist-discovery-worker"
  ])

  common_labels = {
    app       = "argus"
    managedBy = "terraform"
  }
}
