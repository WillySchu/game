# Unity project

Create the Unity 6 LTS project (URP) here as `unity/RaidZero`, then:

1. Add the shared package to `unity/RaidZero/Packages/manifest.json`:
   `"com.raidzero.shared": "file:../../../shared/Game.Shared"`
2. Add Newtonsoft JSON from Package Manager (`com.unity.nuget.newtonsoft-json`). Configure it with a
   `SnakeCaseNamingStrategy` so item trees match the backend's payloads.
3. In Project Settings > Editor, set Asset Serialization to **Force Text** and Version Control to **Visible Meta Files**.

See *Raid Zero: Unity Client & Game Server Plan* for the folder layout, assemblies and build order.
