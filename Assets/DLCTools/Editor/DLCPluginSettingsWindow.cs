// ============================================================
//  DLCPluginSettingsWindow.cs  —  DLCTools (Editor)
// ============================================================
//  Menu: SVU DLC ▸ Plugin Settings…
//
//  The one place to edit the plugin's identity. Writes manifest.json
//  (what the host reads) and keeps the plugin class's Id / Version
//  literals in step with it.
//
//  Changing the plugin name or organisation domain renames the
//  plugin. For name "Galaxy", domain "swinburne.edu.au":
//
//    Package ID                        au.edu.swinburne.galaxy
//    Folder / assembly / plugin class  GalaxyDLC
//    Content scene                     GalaxyScene.unity
//    Content root / address            GalaxyContent
//    Example script / material         GalaxyBehaviour / GalaxyMaterial
//
//  Every occurrence of the old names inside the plugin folder is
//  replaced, file names included. Files move with their .meta files,
//  so GUIDs and references survive. Build output carrying the old
//  names is deleted — build again afterwards. Not undoable: commit
//  to git first.
// ============================================================

#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DLCFramework;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DLCTools
{
    public class DLCPluginSettingsWindow : EditorWindow
    {
        private static readonly string[] TypeLabels = { "3D", "2D" };
        private static readonly string[] TypeValues = { "3d", "2d" };
        private static readonly string[] TextExtensions =
            { ".cs", ".asmdef", ".json", ".unity", ".prefab", ".mat", ".asset", ".md", ".txt" };

        private DLCPluginSettings _s;
        private string            _error;

        // Edited values (serialized so they survive a domain reload)
        [SerializeField] private bool   _loaded;
        [SerializeField] private string _name, _domain, _displayName, _version, _author, _description;
        [SerializeField] private int    _type;
        [SerializeField] private string _origName, _origId;
        [SerializeField] private string _origDisplayName, _origVersion, _origAuthor, _origDescription;
        [SerializeField] private int    _origType;

        public static void Open()
        {
            var w = GetWindow<DLCPluginSettingsWindow>(true, "Plugin Settings");
            w.minSize = new Vector2(500, 400);
            w.Load();
            w.Show();
        }

        private void OnEnable()
        {
            if (_loaded) DLCPluginSettings.TryLocate(out _s, out _error);
            else         Load();
        }

        private void Load()
        {
            _loaded = false;
            if (!DLCPluginSettings.TryLocate(out _s, out _error)) return;

            var m = DLCManifest.FromJson(File.ReadAllText(_s.ManifestJsonPath));
            _name        = _origName        = _s.PluginName;
            _domain      = DomainFromId(m.id);
            _origId      = m.id;
            _displayName = _origDisplayName = m.name        ?? "";
            _version     = _origVersion     = m.version     ?? "1.0.0";
            _author      = _origAuthor      = m.author      ?? "";
            _description = _origDescription = m.description ?? "";
            _type        = _origType        = m.dlcType == "2d" ? 1 : 0;
            _loaded = true;
        }

        // ── Derived ───────────────────────────────────────────────

        private string NewId => IdFrom(_domain, _name);

        private bool NameFollowsConvention => _s != null && _s.AssemblyName == _s.PluginName + "DLC";

        private string NewFolder
        {
            get
            {
                string old = _s.PluginFolder;
                if (_name == _origName || Path.GetFileName(old) != _origName + "DLC") return old;
                return Path.GetDirectoryName(old).Replace('\\', '/') + "/" + _name + "DLC";
            }
        }

        private bool IsRename   => _name != _origName || NewId != _origId;
        private bool HasChanges => IsRename
                                || _displayName != _origDisplayName || _version != _origVersion
                                || _author != _origAuthor || _description != _origDescription
                                || _type != _origType;

        private static string DomainFromId(string id)
        {
            var parts = (id ?? "").Split('.');
            return parts.Length < 2 ? "" : string.Join(".", parts.Take(parts.Length - 1).Reverse());
        }

        private static string IdFrom(string domain, string name)
            => string.Join(".", (domain ?? "").ToLowerInvariant().Split('.').Reverse())
               + "." + (name ?? "").ToLowerInvariant();

        // ── GUI ───────────────────────────────────────────────────

        private void OnGUI()
        {
            if (_s == null || !_loaded)
            {
                EditorGUILayout.HelpBox(_error ?? "No plugin found.", MessageType.Error);
                if (GUILayout.Button("Retry")) Load();
                return;
            }

            EditorGUILayout.LabelField("Identity", EditorStyles.boldLabel);
            string nameBefore = _name;
            using (new EditorGUI.DisabledScope(!NameFollowsConvention))
                _name = EditorGUILayout.TextField(new GUIContent("Plugin Name",
                            "PascalCase, e.g. Galaxy → GalaxyDLC, GalaxyContent, GalaxyScene"), _name).Trim();
            if (_name != nameBefore && _displayName == nameBefore + " DLC")
                _displayName = _name + " DLC";   // default display name follows the plugin name
            if (!NameFollowsConvention)
                EditorGUILayout.HelpBox($"Assembly '{_s.AssemblyName}' doesn't follow the <Name>DLC convention, " +
                                        "so the plugin can't be renamed here.", MessageType.Info);

            _domain = EditorGUILayout.TextField(new GUIContent("Organisation Domain",
                          "Your organisation's web domain, e.g. swinburne.edu.au"), _domain)
                          .Trim().ToLowerInvariant();

            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.TextField(new GUIContent("Package ID",
                    "Derived from domain and name. Unique identity of the plugin on the host."), NewId);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Details", EditorStyles.boldLabel);
            _displayName = EditorGUILayout.TextField("Display Name", _displayName);
            _version     = EditorGUILayout.TextField(new GUIContent("Version", "major.minor.patch"), _version).Trim();
            _author      = EditorGUILayout.TextField("Author", _author);
            EditorGUILayout.LabelField("Description");
            _description = EditorGUILayout.TextArea(_description, GUILayout.MinHeight(40));
            _type        = EditorGUILayout.Popup("Content Type", _type, TypeLabels);

            EditorGUILayout.Space();

            var errors = Validate();
            foreach (var e in errors) EditorGUILayout.HelpBox(e, MessageType.Error);

            if (errors.Count == 0 && _domain == "example.com")
                EditorGUILayout.HelpBox("Set your organisation's domain before sharing the plugin — " +
                                        "the package ID must be unique on the host.", MessageType.Warning);

            if (errors.Count == 0 && IsRename)
                EditorGUILayout.HelpBox(
                    $"Applying renames the plugin:\n" +
                    $"  {_s.PluginFolder}  →  {NewFolder}\n" +
                    $"  {_origName}DLC, {_origName}Content, {_origName}Scene …  →  {_name}DLC, {_name}Content, {_name}Scene …\n" +
                    $"  {_origId}  →  {NewId}\n" +
                    "Previous build output is deleted. Commit to git first — this cannot be undone.",
                    MessageType.Warning);

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                GUI.enabled = HasChanges;
                if (GUILayout.Button("Revert", GUILayout.Height(28))) { GUI.FocusControl(null); Load(); }
                GUI.enabled = HasChanges && errors.Count == 0 && !EditorApplication.isCompiling;
                if (GUILayout.Button(IsRename ? "Apply and Rename" : "Apply", GUILayout.Height(28)))
                {
                    GUI.FocusControl(null);
                    Apply();
                }
                GUI.enabled = true;
            }
        }

        private List<string> Validate()
        {
            var errors = new List<string>();

            if (!Regex.IsMatch(_name ?? "", @"^[A-Z][A-Za-z0-9]*$"))
                errors.Add("Plugin Name must be PascalCase letters and digits, starting with a capital (e.g. Galaxy).");
            if (!Regex.IsMatch(_domain ?? "", @"^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$"))
                errors.Add("Organisation Domain must look like a web domain, e.g. swinburne.edu.au.");
            if (!Regex.IsMatch(_version ?? "", @"^\d+\.\d+\.\d+$"))
                errors.Add("Version must be major.minor.patch, e.g. 1.0.0.");
            if (string.IsNullOrWhiteSpace(_displayName))
                errors.Add("Display Name is required.");

            if (errors.Count == 0 && IsRename)
            {
                if (NewFolder != _s.PluginFolder && (AssetDatabase.IsValidFolder(NewFolder) || Directory.Exists(NewFolder)))
                    errors.Add($"{NewFolder} already exists.");
                if (EditorUtility.scriptCompilationFailed)
                    errors.Add("Fix the script compile errors before renaming.");
            }
            return errors;
        }

        // ── Apply ─────────────────────────────────────────────────

        private void Apply()
        {
            string newId     = NewId;
            string newFolder = NewFolder;
            bool   rename    = IsRename;

            if (rename && !EditorUtility.DisplayDialog("Rename plugin",
                    $"Rename {_origName}DLC ({_origId}) to {_name}DLC ({newId})?\n\n" +
                    "Previous build output will be deleted. This cannot be undone.",
                    "Rename", "Cancel"))
                return;

            string reopenScene = null;
            if (rename && !Rename(newId, newFolder, out reopenScene)) return;

            var target = new DLCPluginSettings
            {
                PackageId    = newId,
                AssemblyName = _name + "DLC",
                PluginFolder = newFolder
            };
            if (!rename) target.AssemblyName = _s.AssemblyName;

            // manifest.json — the file the host reads.
            var m = DLCManifest.FromJson(File.ReadAllText(target.ManifestJsonPath));
            m.id           = newId;
            m.assemblyName = target.AssemblyName;
            m.name         = _displayName.Trim();
            m.version      = _version;
            m.author       = _author.Trim();
            m.description  = _description.Trim();
            m.dlcType      = TypeValues[_type];
            File.WriteAllText(target.ManifestJsonPath, JsonUtility.ToJson(m, true));

            // Plugin class Id / Version literals.
            bool literalsOk = target.WritePluginClassLiterals(newId, _version);

            AssetDatabase.Refresh();
            if (reopenScene != null && File.Exists(reopenScene))
                EditorSceneManager.OpenScene(reopenScene, OpenSceneMode.Single);

            if (!literalsOk)
                EditorUtility.DisplayDialog("Plugin class not updated",
                    "manifest.json was saved, but the plugin class's Id / Version could not be found. " +
                    $"Set them by hand to \"{newId}\" and \"{_version}\".", "OK");

            Debug.Log($"[SVU DLC] ✓ Plugin settings saved: {target.AssemblyName} ({newId}) v{_version}" +
                      (rename ? " — renamed. Run Check Content to rebuild." : "."));
            Load();
        }

        private bool Rename(string newId, string newFolder, out string reopenScene)
        {
            reopenScene = null;
            string oldFolder = _s.PluginFolder;
            string oldN      = _origName;
            string newN      = _name;

            // Close any open scene inside the plugin folder so its file can move.
            bool pluginSceneOpen = Enumerable.Range(0, SceneManager.sceneCount)
                .Select(SceneManager.GetSceneAt)
                .Any(sc => sc.path.StartsWith(oldFolder + "/"));
            if (pluginSceneOpen)
            {
                if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return false;
                reopenScene = $"{newFolder}/{newN}Scene.unity";
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
            StageUtility.GoToMainStage();

            // Build output carries the old names.
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            var group    = settings != null ? settings.FindGroup(_s.GroupName) : null;
            if (group != null) settings.RemoveGroup(group);
            if (AssetDatabase.IsValidFolder(_s.BuildFolder)) AssetDatabase.DeleteAsset(_s.BuildFolder);
            AssetDatabase.SaveAssets();

            var replacements = new List<KeyValuePair<string, string>>();
            if (newId != _origId)
                replacements.Add(new KeyValuePair<string, string>(_origId, newId));
            if (newN != oldN)
                foreach (var suffix in new[] { "DLC", "Content", "Scene", "Behaviour", "Material" })
                    replacements.Add(new KeyValuePair<string, string>(oldN + suffix, newN + suffix));
            if (newN != oldN)   // web UI group label in the plugin class
                replacements.Add(new KeyValuePair<string, string>($"\"{oldN}\"", $"\"{newN}\""));

            string ReplaceNames(string text)
            {
                foreach (var r in replacements) text = text.Replace(r.Key, r.Value);
                return text;
            }

            try
            {
                AssetDatabase.StartAssetEditing();

                var files = Directory.GetFiles(oldFolder, "*", SearchOption.AllDirectories)
                                     .Where(f => !f.EndsWith(".meta"))
                                     .ToList();

                // File contents
                foreach (var f in files.Where(f => TextExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())))
                {
                    string text    = File.ReadAllText(f);
                    string updated = ReplaceNames(text);
                    if (updated != text) File.WriteAllText(f, updated);
                }

                // File names, each with its .meta (GUIDs preserved)
                foreach (var f in files)
                {
                    string newName = ReplaceNames(Path.GetFileName(f));
                    if (newName == Path.GetFileName(f)) continue;
                    string dest = Path.Combine(Path.GetDirectoryName(f), newName);
                    File.Move(f, dest);
                    if (File.Exists(f + ".meta")) File.Move(f + ".meta", dest + ".meta");
                }

                // The plugin folder itself
                if (newFolder != oldFolder)
                {
                    Directory.Move(oldFolder, newFolder);
                    if (File.Exists(oldFolder + ".meta")) File.Move(oldFolder + ".meta", newFolder + ".meta");
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            return true;
        }
    }
}
#endif
