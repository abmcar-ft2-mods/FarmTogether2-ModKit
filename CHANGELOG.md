# Changelog

## 1.0.1 - 2026-07-14

- Isolate generated mod repositories from stale packages in the global NuGet cache.
- Preserve multiple declared test projects and guard scripts as distinct build inputs.

## 1.0.0 - 2026-07-14

- Add the source-authored `FarmTogether2.GameApi.Ref` reference package.
- Add compatibility verification against locally generated interop assemblies.
- Add deterministic package tooling for supported mods.
- Add reusable GitHub Actions workflows for build and release pipelines.
- Require explicit read-only authentication when reusable workflows consume the private ModKit repository.
- Keep Hosted and LocalInterop restores on one locked package graph while selecting the correct compile-time reference source for each mode.
- Publish immutable, attested Releases only after candidate assets and repository evidence pass verification.
- Stage and verify matching plugin DLL and PDB files, then replace both with rollback on deployment failure.
