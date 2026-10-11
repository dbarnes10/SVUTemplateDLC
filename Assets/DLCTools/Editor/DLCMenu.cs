// ============================================================
//  DLCMenu.cs  —  DLCTools (Editor)
// ============================================================
//  The single "SVU DLC" menu.
//
//    Plugin Settings…     name, organisation, version, author …
//                         (renames the plugin when name/domain change)
//    Open Content Scene   the scene you build your plugin in
//    Check Content        builds the bundle prefab from the scene and
//                         reports what will and won't ship
//    Build and Export…    Check Content, then the full package
// ============================================================

#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.SceneManagement;

namespace DLCTools
{
    internal static class DLCMenu
    {
        private const string Root = "SVU DLC/";

        [MenuItem(Root + "Plugin Settings…", false, 0)]
        private static void PluginSettings() => DLCPluginSettingsWindow.Open();

        [MenuItem(Root + "Open Content Scene", false, 20)]
        private static void OpenContentScene()
        {
            if (!Locate(out var s)) return;
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(s.ScenePath) == null)
            {
                EditorUtility.DisplayDialog("SVU DLC", $"Content scene not found at {s.ScenePath}.", "OK");
                return;
            }
            if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                EditorSceneManager.OpenScene(s.ScenePath, OpenSceneMode.Single);
        }

        [MenuItem(Root + "Check Content", false, 21)]
        private static void CheckContent()
        {
            if (Locate(out var s)) DLCPrefabBuilder.BuildFromMenu(s);
        }

        [MenuItem(Root + "Build and Export…", false, 40)]
        private static void BuildAndExport() => DLCPackageExporter.Open();

        private static bool Locate(out DLCPluginSettings s)
        {
            if (DLCPluginSettings.TryLocate(out s, out string error)) return true;
            EditorUtility.DisplayDialog("SVU DLC", error, "OK");
            return false;
        }
    }
}
#endif
