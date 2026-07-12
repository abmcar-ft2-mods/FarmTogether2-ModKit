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

## License

FarmTogether2 ModKit is available under the MIT License. Farm Together 2 and its game assemblies are not included and remain subject to their respective terms.
