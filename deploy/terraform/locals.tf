locals {
  # Worker name → RabbitMQ queue name. Workers consume the queue named after
  # their service name by convention (MassTransit / Aspire conventions).
  # Validation worker is a request/reply HTTP service (not queue-driven) and
  # is omitted; orchestrator-side workers (asset-storage) also use HTTP.
  worker_queues = {
    "amass-worker"              = "amass-worker"
    "asset-scoring-worker"      = "asset-scoring-worker"
    "dns-resolver-worker"       = "dns-resolver-worker"
    "finding-deduper-worker"    = "finding-deduper-worker"
    "fingerprint-worker"        = "fingerprint-worker"
    "headless-spider-worker"    = "headless-spider-worker"
    "html-dom-spider-worker"    = "html-dom-spider-worker"
    "http-probe-worker"         = "http-probe-worker"
    "http-worker"               = "http-worker"
    "js-extractor-worker"       = "js-extractor-worker"
    "regex-scanner-worker"      = "regex-scanner-worker"
    "subfinder-worker"          = "subfinder-worker"
    "wordlist-discovery-worker" = "wordlist-discovery-worker"
  }

  common_labels = {
    app       = "argus"
    managedBy = "terraform"
  }
}
