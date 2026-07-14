# Changelog

## 1.0.0 - 2026-07-13

- Add the source-authored `FarmTogether2.GameApi.Ref` reference package.
- Add compatibility verification against locally generated interop assemblies.
- Add deterministic package tooling for supported mods.
- Add reusable GitHub Actions workflows for build and release pipelines.
- Require explicit read-only authentication when reusable workflows consume the private ModKit repository.
- Publish immutable, attested Releases only after candidate assets and repository evidence pass verification.
- Stage and verify matching plugin DLL and PDB files, then replace both with rollback on deployment failure.
