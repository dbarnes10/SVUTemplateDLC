// ============================================================
//  DLCPackageExporter.cs  —  DLCTools (Editor)
// ============================================================
//  Builds a plugin's bundle prefab, then its Addressables content,
//  and assembles the package folder the host loads:
//
//    {OutputRoot}/{PackageId}/
//      manifest.json                      (unityVersion stamped)
//      code/{AssemblyName}.dll
//      addressables/{Platform}/...        (catalog + bundles)
//      data/...                           (Assets/StreamingAssets, mirrored)
//
//  DATA
//  ────
//  Unity only copies StreamingAssets into player builds, and on the
//  host Application.streamingAssetsPath is the HOST's folder. So the
//  plugin project's Assets/StreamingAssets is mirrored into the
//  package's data/ folder, which plugins reach via IDLCContext.DataPath.
//  The copy is incremental — files whose size and timestamp match
//  are skipped, and files no longer in StreamingAssets are removed —
//  so re-exporting large data is cheap. "Include Data" can be turned
//  off for quick code-only exports (data/ is then left untouched).
//
//  Copy {PackageId}/ into the host's DLC folder:
//    {Application.persistentDataPath}/DLC/{PackageId}/
//
//  Menu: SVU DLC ▸ Build and Export…
//  Generalised from SphereBundleBuilder; behaviour is the same
//  apart from always rebuilding the bundle prefab first.
// ============================================================

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DLCFramework;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;

namespace DLCTools
{
    public class DLCPackageExporter : EditorWindow
    {
        private DLCPluginSettings _s;          // re-located on focus: names can change in Plugin Settings
        private string            _locateError;
        [SerializeField] private string _outputRoot;
        [SerializeField] private bool   _includeData = true;

        // StreamingAssets summary, refreshed on focus (scanning GBs each repaint is too slow).
        private int  _dataFiles;
        private long _dataBytes;

        private static string DataSource => Path.Combine(Application.dataPath, "StreamingAssets");

        public static void Open()
        {
            var w = GetWindow<DLCPackageExporter>(true, "Build and Export");
            w.minSize = new Vector2(460, 230);
            w.Relocate();
            w.Show();
        }

        private void OnEnable() => Relocate();
        private void OnFocus()  => Relocate();

        private void Relocate()
        {
            DLCPluginSettings.TryLocate(out _s, out _locateError);
            if (_s != null && string.IsNullOrEmpty(_outputRoot))
                _outputRoot = EditorPrefs.GetString(PrefsKey(_s), DefaultOutputRoot());
            if (_s != null)
                _includeData = EditorPrefs.GetBool(PrefsKey(_s) + ".IncludeData", true);

            _dataFiles = 0;
            _dataBytes = 0;
            if (Directory.Exists(DataSource))
                foreach (var f in DataFiles(DataSource))
                {
                    _dataFiles++;
                    _dataBytes += new FileInfo(f).Length;
                }
        }

        private static string PrefsKey(DLCPluginSettings s) => $"DLCTools.OutputRoot.{s.PackageId}";

        private static string DefaultOutputRoot()
            => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "DLCOutput"));

        private string DllPath
            => Path.Combine(Application.dataPath, "..", "Library", "ScriptAssemblies", _s.AssemblyName + ".dll");

        // ── GUI ───────────────────────────────────────────────────

        private void OnGUI()
        {
            if (_s == null)
            {
                EditorGUILayout.HelpBox(_locateError ?? "No plugin found.", MessageType.Error);
                return;
            }

            GUILayout.Label($"{_s.PackageId} — Package Export", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                _outputRoot = EditorGUILayout.TextField("Output Root", _outputRoot);
                if (GUILayout.Button("…", GUILayout.Width(28)))
                {
                    string picked = EditorUtility.OpenFolderPanel("DLC output root", _outputRoot, "");
                    if (!string.IsNullOrEmpty(picked)) { _outputRoot = picked; GUI.changed = true; }
                }
                if (EditorGUI.EndChangeCheck()) EditorPrefs.SetString(PrefsKey(_s), _outputRoot);
            }
            EditorGUI.BeginChangeCheck();
            _includeData = EditorGUILayout.Toggle(new GUIContent("Include Data",
                "Mirror Assets/StreamingAssets into the package's data/ folder. " +
                "Turn off for quick code-only exports; data/ is then left as it is."), _includeData);
            if (EditorGUI.EndChangeCheck()) EditorPrefs.SetBool(PrefsKey(_s) + ".IncludeData", _includeData);
            EditorGUILayout.LabelField(" ", _dataFiles == 0
                ? "No Assets/StreamingAssets data"
                : $"Assets/StreamingAssets: {_dataFiles} file(s), {FormatBytes(_dataBytes)}",
                EditorStyles.miniLabel);

            EditorGUILayout.Space();

            string manifestError = ValidateManifest(out _);
            bool manifestOk = manifestError == null;
            bool sourceOk   = AssetDatabase.LoadAssetAtPath<SceneAsset>(_s.ScenePath) != null;
            bool dllOk      = File.Exists(DllPath) && !EditorUtility.scriptCompilationFailed;
            bool platformOk = PlatformFolder(EditorUserBuildSettings.activeBuildTarget) != null;

            DrawCheck(manifestOk ? "manifest.json and plugin class agree" : manifestError, manifestOk);
            DrawCheck($"Content scene: {_s.ScenePath}", sourceOk);
            DrawCheck($"{_s.AssemblyName}.dll compiled without errors", dllOk);
            DrawCheck($"Build target: {EditorUserBuildSettings.activeBuildTarget} " +
                      "(StandaloneWindows64 or StandaloneOSX)", platformOk);

            EditorGUILayout.Space();
            GUI.enabled = manifestOk && sourceOk && dllOk && platformOk && !EditorApplication.isCompiling;
            if (GUILayout.Button("Build and Export Package", GUILayout.Height(36)))
                Export();
            GUI.enabled = true;
        }

        private static void DrawCheck(string label, bool ok)
        {
            var c = GUI.color;
            GUI.color = ok ? new Color(0.4f, 1f, 0.4f) : new Color(1f, 0.4f, 0.4f);
            GUILayout.Label((ok ? "✓ " : "✗ ") + label, EditorStyles.miniLabel);
            GUI.color = c;
        }

        /// <summary>Returns null if manifest.json exists and agrees with the plugin settings.</summary>
        private string ValidateManifest(out string json)
        {
            json = null;
            if (!File.Exists(_s.ManifestJsonPath)) return $"{_s.ManifestJsonPath} not found";
            json = File.ReadAllText(_s.ManifestJsonPath);

            DLCManifest m;
            try   { m = DLCManifest.FromJson(json); }
            catch { return "manifest.json is not valid JSON"; }
            if (m == null)                         return "manifest.json is empty";
            if (m.id != _s.PackageId)              return $"manifest.json id '{m.id}' ≠ '{_s.PackageId}'";
            if (m.assemblyName != _s.AssemblyName) return $"manifest.json assemblyName '{m.assemblyName}' ≠ '{_s.AssemblyName}'";
            if (!m.hasCode || !m.hasAssets)        return "manifest.json must have hasCode and hasAssets = true";

            _s.ReadPluginClassLiterals(out string classId, out string classVersion);
            if (classId == null || classVersion == null)
                return "plugin class Id/Version not found (expected one IDLCPlugin class)";
            if (classId != m.id)
                return $"plugin class Id '{classId}' ≠ manifest id '{m.id}' — fix in Plugin Settings";
            if (classVersion != m.version)
                return $"plugin class Version '{classVersion}' ≠ manifest version '{m.version}' — fix in Plugin Settings";
            return null;
        }

        // ── Export ────────────────────────────────────────────────

        private void Export()
        {
            Relocate();
            if (_s == null) return;
            string tag = _s.LogTag;

            // 1. Bundle prefab + wire manifest (always rebuilt — never ship a stale prefab).
            if (!DLCPrefabBuilder.Build(_s))
            {
                EditorUtility.DisplayDialog("Export failed",
                    "The content could not be built. See the Console.", "OK");
                return;
            }

            var settings = AddressableAssetSettingsDefaultObject.Settings;
            string platform = PlatformFolder(EditorUserBuildSettings.activeBuildTarget);

            string pkgDir  = Path.Combine(_outputRoot, _s.PackageId);
            string destDir = Path.Combine(pkgDir, "addressables", platform);
            Directory.CreateDirectory(destDir);

            // 2. Point the active profile at this package's output + runtime load path.
            string profileId = settings.activeProfileId;
            string loadPath  = $"{{UnityEngine.Application.persistentDataPath}}/DLC/{_s.PackageId}/addressables/{platform}";
            settings.profileSettings.SetValue(profileId, AddressableAssetSettings.kLocalBuildPath, destDir);
            settings.profileSettings.SetValue(profileId, AddressableAssetSettings.kLocalLoadPath,  loadPath);
            settings.BuildRemoteCatalog = true;
            settings.RemoteCatalogBuildPath.SetVariableByName(settings, AddressableAssetSettings.kLocalBuildPath);
            settings.RemoteCatalogLoadPath .SetVariableByName(settings, AddressableAssetSettings.kLocalLoadPath);
            AssetDatabase.SaveAssets();

            // 3. Addressables content.
            AddressableAssetSettings.BuildPlayerContent(out var result);
            if (!string.IsNullOrEmpty(result.Error))
            {
                Debug.LogError($"{tag} Addressables build FAILED: {result.Error}");
                return;
            }

            // 4. manifest.json, stamped with this editor's exact Unity version
            //    (the host rejects packages built with a different version).
            ValidateManifest(out string json);
            json = Regex.Replace(json,
                "\"unityVersion\"\\s*:\\s*\"[^\"]*\"",
                "\"unityVersion\": \"" + Application.unityVersion + "\"");
            File.WriteAllText(Path.Combine(pkgDir, "manifest.json"), json);

            // 5. Plugin DLL.
            string codeDir = Path.Combine(pkgDir, "code");
            Directory.CreateDirectory(codeDir);
            File.Copy(DllPath, Path.Combine(codeDir, _s.AssemblyName + ".dll"), true);

            // 6. Data: Assets/StreamingAssets → data/ (mirrored, incremental).
            string dataDir = Path.Combine(pkgDir, "data");
            if (!_includeData)
            {
                Debug.Log($"{tag} Data not updated (Include Data is off).");
            }
            else if (!Directory.Exists(DataSource))
            {
                if (Directory.Exists(dataDir)) Directory.Delete(dataDir, true);
            }
            else if (!MirrorData(DataSource, dataDir, tag))
            {
                Debug.LogWarning($"{tag} Data copy cancelled — the package's data/ folder is incomplete. " +
                                 "Export again to finish; files already copied are skipped.");
                return;
            }

            AssetDatabase.Refresh();
            Debug.Log($"{tag} ✓ Package exported: {pkgDir}");
            EditorUtility.RevealInFinder(pkgDir);
        }

        // ── Data mirroring ────────────────────────────────────────

        private static IEnumerable<string> DataFiles(string root)
            => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                        .Where(f => !f.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)
                                 && Path.GetFileName(f) != ".DS_Store");

        /// <summary>
        /// Make dst an exact copy of src (Unity .meta files excluded). Files whose
        /// size matches and whose timestamp is within 2 s (FAT/exFAT resolution)
        /// are skipped. Returns false if the user cancelled.
        /// </summary>
        private static bool MirrorData(string src, string dst, string tag)
        {
            int copied = 0, skipped = 0, deleted = 0;
            long copiedBytes = 0;
            var keep  = new HashSet<string>();
            var files = DataFiles(src).ToList();
            double lastUi = 0;

            try
            {
                Directory.CreateDirectory(dst);

                for (int i = 0; i < files.Count; i++)
                {
                    string rel    = Path.GetRelativePath(src, files[i]);
                    string target = Path.Combine(dst, rel);
                    keep.Add(rel);

                    if (EditorApplication.timeSinceStartup - lastUi > 0.1)
                    {
                        lastUi = EditorApplication.timeSinceStartup;
                        if (EditorUtility.DisplayCancelableProgressBar("Copying data",
                                $"{rel}  ({i + 1}/{files.Count})", (float)i / files.Count))
                            return false;
                    }

                    var si = new FileInfo(files[i]);
                    var ti = new FileInfo(target);
                    if (ti.Exists && ti.Length == si.Length &&
                        Math.Abs((ti.LastWriteTimeUtc - si.LastWriteTimeUtc).TotalSeconds) < 2.0)
                    {
                        skipped++;
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.Copy(files[i], target, true);
                    File.SetLastWriteTimeUtc(target, si.LastWriteTimeUtc);
                    copied++;
                    copiedBytes += si.Length;
                }

                // Remove files no longer in StreamingAssets, then empty folders.
                foreach (var f in Directory.GetFiles(dst, "*", SearchOption.AllDirectories))
                    if (!keep.Contains(Path.GetRelativePath(dst, f)))
                    {
                        File.Delete(f);
                        deleted++;
                    }
                foreach (var d in Directory.GetDirectories(dst, "*", SearchOption.AllDirectories)
                                           .OrderByDescending(d => d.Length))
                    if (!Directory.EnumerateFileSystemEntries(d).Any())
                        Directory.Delete(d);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            Debug.Log($"{tag} Data: {copied} file(s) copied ({FormatBytes(copiedBytes)}), " +
                      $"{skipped} unchanged, {deleted} removed.");
            return true;
        }

        private static string FormatBytes(long b)
        {
            if (b >= 1L << 30) return $"{b / (double)(1L << 30):0.00} GB";
            if (b >= 1L << 20) return $"{b / (double)(1L << 20):0.0} MB";
            if (b >= 1L << 10) return $"{b / (double)(1L << 10):0.0} KB";
            return $"{b} B";
        }

        private static string PlatformFolder(BuildTarget t)
        {
            switch (t)
            {
                case BuildTarget.StandaloneWindows:
                case BuildTarget.StandaloneWindows64: return "StandaloneWindows64";
                case BuildTarget.StandaloneOSX:       return "StandaloneOSX";
                default:                              return null;
            }
        }
    }
}
#endif
