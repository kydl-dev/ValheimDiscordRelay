// Editor-only. Keeps Assets/Kydl.Ui an exact copy of the shared UI kit of all the user's mods
// (the shared Kydl.Ui folder), so the Unity preview shows the same look the mod gets when it is built
// (the mod compiles the shared files directly from Claude\shared).
//
//  * Runs when the Editor loads / recompiles and from the menu "Kydl > Sync shared UI kit".
//  * Copies only files that differ, never deletes anything else in the project. Change the kit in Claude\shared, never in Assets/Kydl.Ui.
using System.IO;
using UnityEditor;
using UnityEngine;

namespace ValheimDiscordRelay.EditorTools
{
    [InitializeOnLoad]
    public static class KydlUiSync
    {
        // <Main>/<Project>-Unity -> <Main> -> the projects root -> shared Kydl.Ui
        private const string SharedRelative = "../../Claude/shared/Kydl.Ui";
        private const string TargetRelative = "Assets/Kydl.Ui";

        static KydlUiSync()
        {
            EditorApplication.delayCall += () => Sync(false);
        }

        private static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        [MenuItem("Kydl/Sync shared UI kit")]
        public static void SyncFromMenu() { Sync(true); }

        /// Returns the number of files copied.
        public static int Sync(bool verbose)
        {
            string source = Path.GetFullPath(Path.Combine(ProjectRoot, SharedRelative));
            string target = Path.Combine(ProjectRoot, TargetRelative);
            if (!Directory.Exists(source))
            {
                Debug.LogWarning("[KydlUiSync] Shared UI kit not found at " + source + " - the preview keeps its current copy.");
                return 0;
            }

            int copied = 0;
            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                string rel = file.Substring(source.Length).TrimStart('\\', '/');
                if (rel.EndsWith(".meta") || rel.EndsWith(".md")) continue;
                string dst = Path.Combine(target, rel);
                if (File.Exists(dst) && Same(file, dst)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                File.Copy(file, dst, true);
                copied++;
            }

            if (copied > 0)
            {
                AssetDatabase.Refresh();
                Debug.Log("[KydlUiSync] " + copied + " file(s) of the shared UI kit copied to " + TargetRelative);
            }
            else if (verbose)
            {
                Debug.Log("[KydlUiSync] " + TargetRelative + " is already identical to the shared UI kit.");
            }

            return copied;
        }

        private static bool Same(string a, string b)
        {
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            if (fa.Length != fb.Length) return false;
            byte[] ba = File.ReadAllBytes(a), bb = File.ReadAllBytes(b);
            for (int i = 0; i < ba.Length; i++) if (ba[i] != bb[i]) return false;
            return ba.Length == bb.Length;
        }
    }
}
