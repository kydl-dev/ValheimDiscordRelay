# Assets

Drop zone for build output. Filled automatically:

- **Plugin DLL** - copied here (and to `../build`) after every build by `Claude\shared\Kydl.Mod.targets`. `*.dll` here is git-ignored.
- **AssetBundle `valheimdiscordrelay`** - copied here by the Unity editor script `Assets/Editor/ModBundleExporter.cs`. It is embedded into the DLL
  on the next mod build (players download one file). Bundles are tracked in git.

Do not hand-edit files in here; rebuild instead.
