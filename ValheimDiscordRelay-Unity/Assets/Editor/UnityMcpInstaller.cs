// Editor-only. Installs "MCP for Unity" (CoplayDev/unity-mcp) into this project the first time it is opened,
// so every project created from the scaffold can talk to Claude without a manual Package Manager step.
//
//  * Checks Packages/manifest.json for the package; if missing, calls Package Manager's Add with the git URL.
//  * Runs once per machine+project: after a successful install it will not re-add the package, so removing it on
//    purpose sticks. A failed install (no internet, git missing) is retried on the next Editor start.
//  * Skipped in batch mode (agents/CI), where the async install would be cut off by -quit.
//  * Requires Git installed (Package Manager uses it for git URLs).
//  * Manual fallback: Window > Package Manager > + > Add package from git URL > PackageUrl below.
using System.IO;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace ValheimDiscordRelay.EditorTools
{
    [InitializeOnLoad]
    public static class UnityMcpInstaller
    {
        private const string PackageName = "com.coplaydev.unity-mcp";

        // Tracks the "main" branch. To pin a version, replace #main with a tag or commit, e.g. #v10.3.0
        private const string PackageUrl = "https://github.com/CoplayDev/unity-mcp.git?path=/MCPForUnity#main";

        private const string DoneKeyPrefix = "ValheimDiscordRelay.UnityMcpInstalled|";

        private static AddRequest _request;

        static UnityMcpInstaller()
        {
            // Defer until the Editor finished loading; Package Manager calls during domain reload are unreliable.
            EditorApplication.delayCall += InstallIfMissing;
        }

        private static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        private static string DoneKey => DoneKeyPrefix + ProjectRoot;

        private static void InstallIfMissing()
        {
            if (Application.isBatchMode)
            {
                return;
            }

            if (IsListedInManifest() || Directory.Exists(Path.Combine(ProjectRoot, "Packages", PackageName)))
            {
                EditorPrefs.SetBool(DoneKey, true);
                return;
            }

            if (EditorPrefs.GetBool(DoneKey, false))
            {
                // It was installed here before and has since been removed on purpose - respect that.
                return;
            }

            Debug.Log($"[UnityMcpInstaller] Installing {PackageName} from {PackageUrl} ...");
            _request = Client.Add(PackageUrl);
            EditorApplication.update += PollRequest;
        }

        private static bool IsListedInManifest()
        {
            string manifestPath = Path.Combine(ProjectRoot, "Packages", "manifest.json");
            return File.Exists(manifestPath) && File.ReadAllText(manifestPath).Contains("\"" + PackageName + "\"");
        }

        private static void PollRequest()
        {
            if (_request == null || !_request.IsCompleted)
            {
                return;
            }

            EditorApplication.update -= PollRequest;

            if (_request.Status == StatusCode.Success)
            {
                EditorPrefs.SetBool(DoneKey, true);
                Debug.Log("[UnityMcpInstaller] MCP for Unity installed. Next: Window > MCP for Unity > Configure All Detected Clients.");
            }
            else
            {
                string reason = _request.Error != null ? _request.Error.message : "unknown error";
                Debug.LogWarning($"[UnityMcpInstaller] Install failed: {reason}. Is Git installed and on PATH? Retrying next Editor start, or add it manually: Package Manager > + > Add package from git URL > {PackageUrl}");
            }

            _request = null;
        }
    }
}
