# Aspirate Manifests

Aspirate is the bridge between the Aspire AppHost topology and the Kubernetes manifests Terraform can apply to GKE.

## Generate

From the repository root:

```bash
dotnet tool restore
cd src/Argus.AppHost
aspirate generate --non-interactive --disable-secrets
```

By default, Aspirate writes Kubernetes manifests under `src/Argus.AppHost/aspirate-output`.

Use `aspirate init` from `src/Argus.AppHost` to set the Artifact Registry host and image tag defaults for the environment. Use `aspirate build` when you only need to rebuild and push containers from the Aspire manifest.

## Apply With Terraform

Terraform can apply the generated YAML after the GKE cluster exists:

```bash
terraform -chdir=deploy/terraform apply \
  -var='project_id=project-30b3b95e-ed2b-4573-98a' \
  -var='apply_aspirate_manifests=true' \
  -var='aspirate_manifest_dir=../../src/Argus.AppHost/aspirate-output'
```

Keep generated secrets out of source. If Aspirate secret management is enabled, provide the decryption password only in CI/CD secret storage.
