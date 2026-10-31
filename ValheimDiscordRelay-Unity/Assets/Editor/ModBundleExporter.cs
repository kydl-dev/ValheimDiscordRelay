// Editor-only. Keeps the mod project's Assets folder in sync with the AssetBundles built in Unity.
//
//  * Every bundle goes to THREE places (user, 2026-10-21): ../ValheimDiscordRelay-Mod/Assets (embedded into the DLL on the next mod
//    build), ../ValheimDiscordRelay-Mod/build (the real, current files) and the Gale TEST profile's BepInEx/plugins/ValheimDiscordRelay/
//    (the mod's own folder; skipped when that profile does not exist on this PC).
//  * Watches <UnityProject>/AssetBundles. Whenever bundles appear/change there (from ANY builder:
//    this script's menu item, AssetBundle Browser, a custom build script...) they are copied to
//    ../ValheimDiscordRelay-Mod/Assets after a short debounce.
//  * Menu "Valheim/Build AssetBundles (and copy to Mod)" builds every bundle assigned in the
//    Inspector and copies the result.
//  * Menu "Valheim/Copy AssetBundles to Mod" copies without building.
//  * Batch mode (agents/CI):
//      Unity.exe -batchmode -nographics -quit -projectPath "<ValheimDiscordRelay-Unity>" -logFile -
//                -executeMethod ValheimDiscordRelay.EditorTools.ModBundleExporter.BuildAndCopy
using System;
using System.IO;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace ValheimDiscordRelay.EditorTools
{
    [InitializeOnLoad]
    public static class ModBundleExporter
    {
        // Output folder for built bundles, relative to the Unity project root.
        private const string BundleOutputFolderName = "AssetBundles";

        // Destination, relative to the Unity project root (sibling mod project).
        private const string ModAssetsRelativePath = "../ValheimDiscordRelay-Mod/Assets";
        private const string ModBuildRelativePath = "../ValheimDiscordRelay-Mod/build";

        // Gale test profile, the mod's own plugin folder (same place the mod build deploys the DLL to: Claude/shared/Kydl.Mod.targets).
        // %APPDATA% is expanded at run time: no PC path in the file (PROJECT_RULES.md section 19).
        private static readonly string GalePluginsPath = System.Environment.ExpandEnvironmentVariables(@"%APPDATA%\com.kesomannen.gale\valheim\profiles\test\BepInEx\plugins");
        private const string GaleModFolder = "ValheimDiscordRelay";

        // Options for the menu/batch build below. Match whatever your existing builder uses
        // (e.g. BuildAssetBundleOptions.UncompressedAssetBundle) so both produce the same bundle.
        private const BuildAssetBundleOptions BundleBuildOptions = BuildAssetBundleOptions.None;

        // The per-bundle .manifest files are only useful for debugging; the mod does not need them.
        private const bool CopyManifests = false;

        // Wait this long after the last file change before copying (Unity writes bundles in pieces).
        private const double DebounceSeconds = 1.0;

        private static FileSystemWatcher _watcher;
        private static volatile bool _copyPending;
        private static long _lastChangeTicks;

        private static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        private static string BundleOutputPath => Path.Combine(ProjectRoot, BundleOutputFolderName);
        private static string ModAssetsPath => Path.GetFullPath(Path.Combine(ProjectRoot, ModAssetsRelativePath));
        private static string ModBuildPath => Path.GetFullPath(Path.Combine(ProjectRoot, ModBuildRelativePath));

        // Extra copies of every bundle: mod build folder, and the Gale test profile when it exists on this PC.
        private static string[] ExtraDestinations()
        {
            var list = new System.Collections.Generic.List<string> { ModBuildPath };
            if (Directory.Exists(GalePluginsPath))
            {
                list.Add(Path.Combine(GalePluginsPath, GaleModFolder));
            }

            return list.ToArray();
        }

        static ModBundleExporter()
        {
            StartWatcher();
            EditorApplication.update += OnEditorUpdate;
            AssemblyReloadEvents.beforeAssemblyReload += StopWatcher;
            EditorApplication.quitting += StopWatcher;
        }

        [MenuItem("Valheim/Build AssetBundles (and copy to Mod)")]
        public static void BuildAndCopy()
        {
            Directory.CreateDirectory(BundleOutputPath);

            // StandaloneWindows64 needs the Windows build module installed in this Editor.
            AssetBundleManifest manifest = BuildPipeline.BuildAssetBundles(
                BundleOutputPath,
                BundleBuildOptions,
                BuildTarget.StandaloneWindows64);

            if (manifest == null)
            {
                Debug.LogError("[ModBundleExporter] AssetBundle build failed - see the Console above. Nothing was copied.");
                if (Application.isBatchMode)
                {
                    EditorApplication.Exit(1);
                }

                return;
            }

            CopyBundles();
        }

        [MenuItem("Valheim/Copy AssetBundles to Mod")]
        public static void CopyBundles()
        {
            _copyPending = false;

            string source = BundleOutputPath;
            string destination = ModAssetsPath;

            if (!Directory.Exists(source))
            {
                Debug.LogWarning($"[ModBundleExporter] Nothing to copy: {source} does not exist yet.");
                return;
            }

            string modRoot = Path.GetDirectoryName(destination);
            if (string.IsNullOrEmpty(modRoot) || !Directory.Exists(modRoot))
            {
                Debug.LogWarning($"[ModBundleExporter] Mod project folder not found: {modRoot}. Nothing was copied.");
                return;
            }

            Directory.CreateDirectory(destination);
            string[] extra = ExtraDestinations();
            foreach (string d in extra)
            {
                Directory.CreateDirectory(d);
            }

            int copied = 0;
            foreach (string file in Directory.GetFiles(source))
            {
                string fileName = Path.GetFileName(file);
                bool isManifest = fileName.EndsWith(".manifest", StringComparison.OrdinalIgnoreCase);
                string bundleName = isManifest ? Path.GetFileNameWithoutExtension(fileName) : fileName;

                // Unity also writes a master bundle + manifest named after the output folder; the mod does not use it.
                if (string.Equals(bundleName, BundleOutputFolderName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (isManifest && !CopyManifests)
                {
                    continue;
                }

                try
                {
                    File.Copy(file, Path.Combine(destination, fileName), true);
                    foreach (string d in extra)
                    {
                        File.Copy(file, Path.Combine(d, fileName), true);
                    }

                    copied++;
                }
                catch (IOException ex)
                {
                    // Usually a file still being written; the next change event retries.
                    Debug.LogWarning($"[ModBundleExporter] Could not copy {fileName}: {ex.Message}");
                    _lastChangeTicks = DateTime.UtcNow.Ticks;
                    _copyPending = true;
                }
            }

            Debug.Log($"[ModBundleExporter] Copied {copied} file(s) to {destination} and {string.Join(", ", extra)}");
        }

        private static void StartWatcher()
        {
            try
            {
                Directory.CreateDirectory(BundleOutputPath);

                _watcher = new FileSystemWatcher(BundleOutputPath)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true
                };
                _watcher.Created += OnOutputChanged;
                _watcher.Changed += OnOutputChanged;
                _watcher.Renamed += OnOutputChanged;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ModBundleExporter] Could not watch {BundleOutputFolderName}: {ex.Message}. Use the Valheim menu instead.");
            }
        }

        private static void StopWatcher()
        {
            if (_watcher == null)
            {
                return;
            }

            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }

        // Runs on a thread-pool thread: touch no Unity APIs here, only flip flags.
        private static void OnOutputChanged(object sender, FileSystemEventArgs e)
        {
            Interlocked.Exchange(ref _lastChangeTicks, DateTime.UtcNow.Ticks);
            _copyPending = true;
        }

        private static void OnEditorUpdate()
        {
            if (!_copyPending)
            {
                return;
            }

            long elapsedTicks = DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastChangeTicks);
            if (elapsedTicks >= (long)(DebounceSeconds * TimeSpan.TicksPerSecond))
            {
                CopyBundles();
            }
        }
    }
}
