# FarmTogether2 ModKit

FarmTogether2 ModKit provides source-authored build contracts and reusable tooling for Farm Together 2 BepInEx IL2CPP mods. It lets GitHub-hosted builds compile plugins without redistributing game assemblies or generated interop files.

The reference assemblies are compile-time contracts only. They contain signatures required by supported mods and must never be installed into the game or loaded at runtime. Local compatibility checks compare these contracts with interop assemblies generated from a user's own game installation.

The reference package models these assembly identities:

- `Assembly-CSharp`
- `Il2Cppmscorlib`
- `MilkstoneUnityExtensions`
- `UnityEngine.CoreModule`
- `UnityEngine.IMGUIModule`
- `UnityEngine.InputLegacyModule`
- `UnityEngine.TextRenderingModule`

This project is an independent community project. It is not affiliated with, endorsed by, or supported by Milkstone Studios.

## Private repository setup

The generated caller workflows expect the ModKit and mod repositories to remain private. Before enabling CI for a generated mod repository:

- Allow private repositories owned by the same GitHub user to call this repository's actions and reusable workflows.
- Add `MODKIT_READ_TOKEN` as an Actions repository secret in the mod repository. The value must be a fine-grained personal access token limited to `FarmTogether2-ModKit` with read-only Contents permission. If Dependabot pull requests must run CI, also create a Dependabot repository secret named `MODKIT_READ_TOKEN` with the same value.
- Enable immutable releases in this repository and every mod repository before pushing the first release tag. The workflows create a draft, verify its assets, then require the published Release to be immutable and accompanied by a GitHub attestation.

The caller's `GITHUB_TOKEN` remains scoped to the mod repository. The separate read token is used only to retrieve the locked private ModKit checkout and Release asset.

Do not grant outside collaborators workflow access to a caller repository unless they may also inspect logs produced by this private ModKit workflow. GitHub treats that as indirect access to the private workflow repository.

## License

FarmTogether2 ModKit is available under the MIT License. Farm Together 2 and its game assemblies are not included and remain subject to their respective terms.
