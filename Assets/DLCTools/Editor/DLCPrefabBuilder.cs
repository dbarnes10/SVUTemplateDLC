// ============================================================
//  DLCPrefabBuilder.cs  —  DLCTools (Editor)
// ============================================================
//  Turns the plugin's content scene into a bundle-safe prefab
//  plus a wire manifest. Menu: SVU DLC ▸ Check Content (also run
//  automatically by Build and Export).
//
//  THE CONTENT SCENE
//  ─────────────────
//  {Plugin}Scene.unity has one root GameObject named {Plugin}Content.
//  Everything under it ships; everything else in the scene (preview
//  camera, stand-ins, test objects) is preview-only. The root's
//  position and rotation in the scene are for preview only — the
//  built prefab's root is reset to the origin.
//
//  WHY SCRIPTS ARE STRIPPED
//  ────────────────────────
//  The host loads the plugin DLL from bytes, so Unity cannot bind
//  plugin MonoBehaviours when it deserialises the bundle — they
//  would arrive as "missing script". The builder removes them from
//  the built prefab and records everything needed to put them back.
//  At runtime DLCWireRunner (host) re-adds them and restores their
//  fields inside context.InstantiatePrefab(), before Awake runs.
//
//  WHAT IT DOES
//  ────────────
//   A. In the content scene: adds a DLCIdentity (stable 8-char id)
//      to every GameObject that carries plugin components or is the
//      target of a reference, and repairs empty/duplicate ids
//      (duplicating a GameObject copies its id). Saves the scene so
//      ids survive rebuilds.
//   B. Copies the content root into a preview scene, unpacks nested
//      prefabs, records every serialized field of every plugin
//      component, removes those components, and saves:
//        Build/{Plugin}Content.prefab        address "{Plugin}Content"
//        Build/{Plugin}Content_wire.json     address "{Plugin}Content_wire"
//      Assets referenced by plugin components are made addressable
//      in the plugin's group automatically.
//
//  CHECKS
//  ──────
//   Errors:   no/duplicate content root; missing scripts; scripts from
//             assemblies the host can't load; Camera, AudioListener or
//             Volume under the root (the host owns these).
//   Warnings: lights outside the root (preview only); lights under
//             the root that aren't Realtime (baked lighting doesn't
//             ship); fields wiring can't restore.
// ============================================================

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using DLCFramework;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace DLCTools
{
    public static class DLCPrefabBuilder
    {
        public const string BuilderVersion = "1.2";

        // ── Menu entry wrapper ────────────────────────────────────

        /// <summary>Build and report the outcome in a dialog (for menu items).</summary>
        public static bool BuildFromMenu(DLCPluginSettings s)
        {
            bool ok = Build(s);
            EditorUtility.DisplayDialog($"{s.AssemblyName} — Check Content",
                ok ? "Content built. See the Console for any warnings."
                   : "Content could not be built. See the Console for details.",
                "OK");
            return ok;
        }

        // ── Build ─────────────────────────────────────────────────

        /// <summary>
        /// Build the bundle prefab and wire manifest from the content scene.
        /// Returns false (with Console errors) if anything blocks the build.
        /// </summary>
        public static bool Build(DLCPluginSettings s)
        {
            string tag = s.LogTag;

            if (EditorApplication.isPlayingOrWillChangePlaymode)
            { Debug.LogError($"{tag} Exit Play Mode before building."); return false; }
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            { Debug.LogError($"{tag} Wait for script compilation to finish, then build again."); return false; }
            if (EditorUtility.scriptCompilationFailed)
            { Debug.LogError($"{tag} Fix the script compile errors first."); return false; }
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(s.ScenePath) == null)
            { Debug.LogError($"{tag} Content scene not found at '{s.ScenePath}'."); return false; }

            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            { Debug.LogError($"{tag} No AddressableAssetSettings found in this project."); return false; }

            // ── Get the content scene, saved ──────────────────────
            Scene scene = SceneManager.GetSceneByPath(s.ScenePath);
            bool openedHere = false;
            if (!scene.IsValid() || !scene.isLoaded)
            {
                scene      = EditorSceneManager.OpenScene(s.ScenePath, OpenSceneMode.Additive);
                openedHere = true;
            }
            else if (scene.isDirty)
            {
                if (!EditorUtility.DisplayDialog("Save content scene?",
                        $"{Path.GetFileName(s.ScenePath)} has unsaved changes. It must be saved before building.",
                        "Save and Continue", "Cancel"))
                    return false;
                EditorSceneManager.SaveScene(scene);
            }

            var report = new Report(tag);
            try
            {
                var root = FindContentRoot(scene, s, report);
                if (root == null) { report.Flush(); return false; }

                CheckScene(scene, root, s, report);
                if (report.HasErrors) { report.Flush(); return false; }

                // ── A. Stable identities in the scene ─────────────
                if (!AssignIdentities(scene, root, s, report)) { report.Flush(); return false; }

                // ── B. Record, strip, save ────────────────────────
                return BuildPrefab(root, s, settings, report);
            }
            catch (Exception e)
            {
                report.Error($"Build threw: {e.Message}\n{e.StackTrace}");
                report.Flush();
                return false;
            }
            finally
            {
                if (openedHere) EditorSceneManager.CloseScene(scene, true);
            }
        }

        // ── Content root & scene checks ───────────────────────────

        private static GameObject FindContentRoot(Scene scene, DLCPluginSettings s, Report report)
        {
            var roots = scene.GetRootGameObjects().Where(g => g.name == s.ContentName).ToList();
            if (roots.Count == 1) return roots[0];

            if (roots.Count == 0)
                report.Error($"{Path.GetFileName(s.ScenePath)} has no root GameObject named '{s.ContentName}'. " +
                             "Everything the plugin ships must sit under that one root.");
            else
                report.Error($"{Path.GetFileName(s.ScenePath)} has {roots.Count} root GameObjects named " +
                             $"'{s.ContentName}'. Keep exactly one.");
            return null;
        }

        private static void CheckScene(Scene scene, GameObject root, DLCPluginSettings s, Report report)
        {
            // Lights outside the root light the preview but never ship.
            foreach (var other in scene.GetRootGameObjects())
            {
                if (other == root) continue;
                foreach (var l in other.GetComponentsInChildren<Light>(true))
                    report.Warn($"Light '{HierarchyPath(l.gameObject, other)}' is outside '{s.ContentName}'. " +
                                "It lights your preview but does not ship — the wall won't have it.");
            }

            foreach (var l in root.GetComponentsInChildren<Light>(true))
                if (l.lightmapBakeType != LightmapBakeType.Realtime)
                    report.Warn($"Light '{HierarchyPath(l.gameObject, root)}' is set to {l.lightmapBakeType}. " +
                                "Baked lighting doesn't ship; set its Mode to Realtime.");

            // Host-owned components.
            foreach (var c in root.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                string what = c is Camera                    ? "a Camera"
                            : c is AudioListener             ? "an AudioListener"
                            : c.GetType().FullName == "UnityEngine.Rendering.Volume" ? "a post-processing Volume"
                            : null;
                if (what != null)
                    report.Error($"'{HierarchyPath(c.gameObject, root)}' has {what}. The host owns these; " +
                                 $"move it outside '{s.ContentName}' if it's for previewing.");
            }
        }

        // ── A. Identities ─────────────────────────────────────────

        private static bool AssignIdentities(Scene scene, GameObject root, DLCPluginSettings s, Report report)
        {
            var scan = Scan(root, s, report);
            if (report.HasErrors) return false;

            var targets = new HashSet<GameObject>(scan.Select(r => r.GameObject));
            foreach (var r in scan)
                foreach (var c in r.Components)
                    foreach (var f in c.Fields)
                        if (f.IsReference && TryGetInHierarchyTarget(f.Read(c.Component), root, out var target))
                            targets.Add(target);

            bool changed = false;
            var  seen    = new HashSet<string>();

            // Repair empty / duplicate ids first, in hierarchy order.
            foreach (var id in root.GetComponentsInChildren<DLCIdentity>(true))
            {
                if (string.IsNullOrEmpty(id.Id) || !seen.Add(id.Id))
                {
                    id.Id   = NewId(seen);
                    EditorUtility.SetDirty(id);
                    changed = true;
                }
            }

            foreach (var go in targets)
            {
                if (go.GetComponent<DLCIdentity>() != null) continue;
                var id = go.AddComponent<DLCIdentity>();
                if (!string.IsNullOrEmpty(id.Id)) seen.Remove(id.Id); // Reset() may have assigned one
                id.Id   = NewId(seen);
                EditorUtility.SetDirty(id);
                changed = true;
            }

            if (changed)
            {
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
                report.Info($"Assigned DLCIdentity ids in {Path.GetFileName(s.ScenePath)}.");
            }
            return true;
        }

        private static string NewId(HashSet<string> seen)
        {
            string id;
            do { id = Guid.NewGuid().ToString("N").Substring(0, 8); }
            while (!seen.Add(id));
            return id;
        }

        // ── B. Prefab ─────────────────────────────────────────────

        private static bool BuildPrefab(GameObject sceneRoot, DLCPluginSettings s,
                                        AddressableAssetSettings settings, Report report)
        {
            var preview = EditorSceneManager.NewPreviewScene();
            try
            {
                // Copy the root into the preview scene without touching the content scene.
                var holder = new GameObject("holder");
                SceneManager.MoveGameObjectToScene(holder, preview);
                var copy = Object.Instantiate(sceneRoot, holder.transform, false);
                copy.transform.SetParent(null, false);
                Object.DestroyImmediate(holder);

                copy.name = s.ContentName;
                copy.transform.localPosition = Vector3.zero;
                copy.transform.localRotation = Quaternion.identity;

                // Object.Instantiate does not keep prefab links, but be certain:
                // nested prefab assets would otherwise ship their own plugin scripts.
                foreach (var t in copy.GetComponentsInChildren<Transform>(true).ToList())
                    if (t != null && PrefabUtility.IsOutermostPrefabInstanceRoot(t.gameObject))
                        PrefabUtility.UnpackPrefabInstance(t.gameObject, PrefabUnpackMode.Completely,
                                                           InteractionMode.AutomatedAction);

                var scan = Scan(copy, s, report);
                if (report.HasErrors) { report.Flush(); return false; }

                var group    = GetOrCreateGroup(settings, s.GroupName);
                var manifest = BuildManifest(copy, scan, s, settings, group, report);
                if (report.HasErrors) { report.Flush(); return false; }

                int stripped = Strip(scan, report);

                Directory.CreateDirectory(s.BuildFolder);
                PrefabUtility.SaveAsPrefabAsset(copy, s.BuiltPrefabPath, out bool saved);
                if (!saved)
                {
                    report.Error($"Could not save '{s.BuiltPrefabPath}'.");
                    report.Flush();
                    return false;
                }

                File.WriteAllText(s.WireManifestPath, manifest.ToJson());
                AssetDatabase.ImportAsset(s.WireManifestPath, ImportAssetOptions.ForceUpdate);

                RegisterEntry(settings, group, s.BuiltPrefabPath, s.ContentName);
                RegisterEntry(settings, group, s.WireManifestPath,
                              DLCWireManifest.ManifestAddress(s.ContentName));

                AssetDatabase.SaveAssets();

                report.Info($"✓ Built '{s.ContentName}' → {s.BuiltPrefabPath} " +
                            $"({stripped} plugin component(s) on {scan.Count} GameObject(s) recorded for wiring).");
                report.Flush();
                return true;
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(preview);
            }
        }

        // ── Scan ──────────────────────────────────────────────────

        private sealed class GORecord
        {
            public GameObject       GameObject;
            public string           Path;
            public List<CompRecord> Components = new List<CompRecord>();
        }

        private sealed class CompRecord
        {
            public Component         Component;
            public List<FieldRecord> Fields = new List<FieldRecord>();
        }

        private sealed class FieldRecord
        {
            public FieldInfo Field;
            public string    ValueKind;   // null for references
            public bool      IsReference => ValueKind == null;
            public Object    Read(Component c) => Field.GetValue(c) as Object;
        }

        private static List<GORecord> Scan(GameObject root, DLCPluginSettings s, Report report)
        {
            var result = new List<GORecord>();

            foreach (var go in AllGameObjects(root))
            {
                string path = HierarchyPath(go, root);
                var rec = new GORecord { GameObject = go, Path = path };

                foreach (var c in go.GetComponents<Component>())
                {
                    if (c == null)
                    {
                        report.Error($"'{path}' has a missing script. Remove it or restore the script.");
                        continue;
                    }

                    Type   t   = c.GetType();
                    string asm = t.Assembly.GetName().Name;

                    if (IsHostResolvable(asm)) continue;

                    if (asm != s.AssemblyName)
                    {
                        report.Error($"'{path}' has {t.Name} from assembly '{asm}', which the host cannot load. " +
                                     $"Move its script inside {s.PluginFolder}/ so it compiles into '{s.AssemblyName}'.");
                        continue;
                    }

                    var cr = new CompRecord { Component = c };
                    foreach (var fi in SerializedFields(t))
                    {
                        Type ft = fi.FieldType;
                        string kind = ValueKind(ft);
                        if (kind != null)
                            cr.Fields.Add(new FieldRecord { Field = fi, ValueKind = kind });
                        else if (typeof(Object).IsAssignableFrom(ft))
                            cr.Fields.Add(new FieldRecord { Field = fi, ValueKind = null });
                        else if (UnitySerializesButUnsupported(ft))
                            report.Warn($"'{path}' {t.Name}.{fi.Name} ({Pretty(ft)}) is not restored by wiring. " +
                                        "Initialise it in code (e.g. Awake).");
                    }
                    rec.Components.Add(cr);
                }

                if (rec.Components.Count > 0)
                {
                    rec.Components = OrderByRequirements(rec.Components);
                    result.Add(rec);
                }
            }
            return result;
        }

        /// <summary>
        /// Orders components so any plugin type named in another's [RequireComponent]
        /// comes first. Otherwise AddComponent at runtime would auto-add the
        /// requirement and the runner would then add a duplicate.
        /// Relative order of same-typed components is preserved.
        /// </summary>
        private static List<CompRecord> OrderByRequirements(List<CompRecord> comps)
        {
            var ordered   = new List<CompRecord>();
            var remaining = new List<CompRecord>(comps);
            while (remaining.Count > 0)
            {
                int pick = remaining.FindIndex(c =>
                    !RequiredTypes(c.Component.GetType()).Any(rt =>
                        remaining.Any(o => o != c && rt.IsInstanceOfType(o.Component))));
                if (pick < 0) pick = 0; // cycle — keep original order
                ordered.Add(remaining[pick]);
                remaining.RemoveAt(pick);
            }
            return ordered;
        }

        // ── Manifest ──────────────────────────────────────────────

        [Serializable]
        private class WireRef
        {
            public string wireId;
            public string type;
            public int    index;
            public string address;
        }

        private static DLCWireManifest BuildManifest(GameObject root, List<GORecord> scan,
                                                     DLCPluginSettings s,
                                                     AddressableAssetSettings settings,
                                                     AddressableAssetGroup group,
                                                     Report report)
        {
            var manifest = new DLCWireManifest
            {
                prefabAddress  = s.ContentName,
                builderVersion = BuilderVersion,
                builtAt        = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
            };

            foreach (var rec in scan)
            {
                var identity = rec.GameObject.GetComponent<DLCIdentity>();
                if (identity == null || string.IsNullOrEmpty(identity.Id))
                {
                    report.Error($"'{rec.Path}' has no DLCIdentity after id assignment. Build again.");
                    continue;
                }

                var entry = new DLCWireEntry { wireId = identity.Id, goPath = rec.Path };

                foreach (var cr in rec.Components)
                {
                    var comp = cr.Component;
                    var wc   = new DLCWireComponent { typeName = comp.GetType().FullName };
                    string owner = $"'{rec.Path}' {comp.GetType().Name}";

                    foreach (var f in cr.Fields)
                    {
                        if (!f.IsReference)
                        {
                            string v = SerialiseValue(f.Field.FieldType, f.Field.GetValue(comp));
                            if (v != null)
                                wc.fields.Add(new DLCWireField
                                    { fieldName = f.Field.Name, fieldType = f.ValueKind, value = v });
                            continue;
                        }

                        var obj = f.Read(comp);
                        if (obj == null) continue; // unassigned (or missing) — nothing to restore

                        var field = RecordReference(obj, root, f.Field, $"{owner}.{f.Field.Name}",
                                                    s, settings, group, report);
                        if (field != null) wc.fields.Add(field);
                    }

                    entry.components.Add(wc);
                }

                manifest.entries.Add(entry);
            }
            return manifest;
        }

        private static DLCWireField RecordReference(Object obj, GameObject root, FieldInfo fi, string where,
                                                    DLCPluginSettings s,
                                                    AddressableAssetSettings settings,
                                                    AddressableAssetGroup group,
                                                    Report report)
        {
            // ── Inside the content root: GameObject / Component ───
            if (TryGetInHierarchyTarget(obj, root, out var targetGo))
            {
                var id = targetGo.GetComponent<DLCIdentity>();
                if (id == null || string.IsNullOrEmpty(id.Id))
                {
                    report.Error($"{where}: target '{HierarchyPath(targetGo, root)}' has no DLCIdentity. Build again.");
                    return null;
                }

                if (obj is GameObject)
                    return new DLCWireField
                    {
                        fieldName = fi.Name, fieldType = "gameobject",
                        value     = JsonUtility.ToJson(new WireRef { wireId = id.Id })
                    };

                var c     = (Component)obj;
                var ct    = c.GetType();
                int index = Array.IndexOf(targetGo.GetComponents(ct), c);
                return new DLCWireField
                {
                    fieldName = fi.Name, fieldType = "component",
                    value     = JsonUtility.ToJson(new WireRef
                    {
                        wireId = id.Id,
                        type   = $"{ct.FullName}, {ct.Assembly.GetName().Name}",
                        index  = index
                    })
                };
            }

            // ── Project asset ─────────────────────────────────────
            if (EditorUtility.IsPersistent(obj))
            {
                string path = AssetDatabase.GetAssetPath(obj);

                if (!path.StartsWith("Assets/") && !path.StartsWith("Packages/"))
                {
                    report.Warn($"{where} references built-in resource '{obj.name}', which cannot be " +
                                "made addressable. Assign it in code instead.");
                    return null;
                }

                // Components / GameObjects inside a prefab asset: only the root is loadable by address.
                GameObject assetGo = obj as GameObject ?? (obj as Component)?.gameObject;
                if (assetGo != null && assetGo.transform.parent != null)
                {
                    report.Warn($"{where} references '{obj.name}' inside prefab asset '{path}'. " +
                                "Only a prefab's root can be referenced.");
                    return null;
                }
                if (assetGo != null && ContainsPluginScripts(assetGo, s.AssemblyName))
                    report.Warn($"{where} references prefab '{path}', which contains {s.AssemblyName} " +
                                "scripts. They will arrive as missing scripts — only the content root is wired.");

                string address = EnsureAssetAddress(settings, group, path, s, where, report);
                if (address == null) return null;
                if (AssetDatabase.IsSubAsset(obj) && assetGo == null)
                    address += $"[{obj.name}]";

                return new DLCWireField
                {
                    fieldName = fi.Name, fieldType = "asset",
                    value     = JsonUtility.ToJson(new WireRef { address = address })
                };
            }

            report.Warn($"{where} references '{obj.name}', which is outside '{s.ContentName}'. It cannot be restored.");
            return null;
        }

        private static bool TryGetInHierarchyTarget(Object obj, GameObject root, out GameObject go)
        {
            go = null;
            if (obj == null || EditorUtility.IsPersistent(obj)) return false;
            go = obj as GameObject ?? (obj as Component)?.gameObject;
            return go != null && go.transform.IsChildOf(root.transform);
        }

        private static bool ContainsPluginScripts(GameObject prefab, string assemblyName)
            => prefab.GetComponentsInChildren<MonoBehaviour>(true)
                     .Any(m => m != null && m.GetType().Assembly.GetName().Name == assemblyName);

        // ── Strip ─────────────────────────────────────────────────

        private static int Strip(List<GORecord> scan, Report report)
        {
            int count = 0;
            foreach (var rec in scan)
            {
                var remaining = rec.Components.Select(c => c.Component).ToList();
                while (remaining.Count > 0)
                {
                    // Remove components nothing else still requires first.
                    int pick = remaining.FindIndex(c =>
                        !remaining.Any(o => o != c &&
                            RequiredTypes(o.GetType()).Any(rt => rt.IsInstanceOfType(c))));
                    if (pick < 0)
                    {
                        report.Warn($"'{rec.Path}': circular [RequireComponent] between plugin components.");
                        pick = 0;
                    }
                    Object.DestroyImmediate(remaining[pick]);
                    remaining.RemoveAt(pick);
                    count++;
                }
            }
            return count;
        }

        private static IEnumerable<Type> RequiredTypes(Type t)
        {
            foreach (RequireComponent rc in t.GetCustomAttributes(typeof(RequireComponent), true))
            {
                if (rc.m_Type0 != null) yield return rc.m_Type0;
                if (rc.m_Type1 != null) yield return rc.m_Type1;
                if (rc.m_Type2 != null) yield return rc.m_Type2;
            }
        }

        // ── Addressables ──────────────────────────────────────────

        private static AddressableAssetGroup GetOrCreateGroup(AddressableAssetSettings settings, string name)
        {
            var g = settings.FindGroup(name);
            if (g != null) return g;

            g = settings.CreateGroup(name, false, false, false, settings.DefaultGroup.Schemas);
            var schema = g.GetSchema<BundledAssetGroupSchema>();
            if (schema != null)
            {
                schema.BuildPath.SetVariableByName(settings, AddressableAssetSettings.kLocalBuildPath);
                schema.LoadPath .SetVariableByName(settings, AddressableAssetSettings.kLocalLoadPath);
            }
            return g;
        }

        private static void RegisterEntry(AddressableAssetSettings settings, AddressableAssetGroup group,
                                          string assetPath, string address)
        {
            string guid  = AssetDatabase.AssetPathToGUID(assetPath);
            var    entry = settings.CreateOrMoveEntry(guid, group, false, false);
            entry.address = address;
            settings.SetDirty(AddressableAssetSettings.ModificationEvent.EntryMoved, entry, true);
        }

        private static string EnsureAssetAddress(AddressableAssetSettings settings, AddressableAssetGroup group,
                                                 string path, DLCPluginSettings s, string where, Report report)
        {
            string guid  = AssetDatabase.AssetPathToGUID(path);
            var    entry = settings.FindAssetEntry(guid, true);
            if (entry != null) return entry.address;

            entry = settings.CreateOrMoveEntry(guid, group, false, false);
            if (entry == null)
            {
                report.Warn($"{where}: '{path}' could not be made addressable.");
                return null;
            }
            entry.address = $"{s.ContentName}/{Path.GetFileNameWithoutExtension(path)}_{guid.Substring(0, 8)}";
            settings.SetDirty(AddressableAssetSettings.ModificationEvent.EntryMoved, entry, true);
            report.Info($"Made '{path}' addressable as '{entry.address}' (referenced by {where}).");
            return entry.address;
        }

        // ── Field helpers ─────────────────────────────────────────

        private static bool IsHostResolvable(string asm)
            => asm.StartsWith("UnityEngine")
            || asm.StartsWith("Unity.")
            || asm.StartsWith("com.unity")
            || asm == "DLCFramework.Interfaces"
            || asm == "mscorlib"
            || asm == "netstandard";

        /// <summary>Instance fields Unity serializes, most-derived first, up to MonoBehaviour.</summary>
        private static IEnumerable<FieldInfo> SerializedFields(Type type)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                       BindingFlags.Instance | BindingFlags.DeclaredOnly;
            var seen = new HashSet<string>();
            for (var t = type; t != null && t != typeof(MonoBehaviour) && t != typeof(Component); t = t.BaseType)
            {
                foreach (var fi in t.GetFields(flags))
                {
                    if (fi.IsStatic || fi.IsInitOnly || fi.IsLiteral || fi.IsNotSerialized) continue;
                    if (!fi.IsPublic && !fi.IsDefined(typeof(SerializeField), true)) continue;
                    if (fi.IsDefined(typeof(SerializeReference), true)) continue;
                    if (!seen.Add(fi.Name)) continue;
                    yield return fi;
                }
            }
        }

        private static string ValueKind(Type t)
        {
            if (t == typeof(float))          return "float";
            if (t == typeof(int))            return "int";
            if (t == typeof(bool))           return "bool";
            if (t == typeof(string))         return "string";
            if (t == typeof(Vector2))        return "vector2";
            if (t == typeof(Vector3))        return "vector3";
            if (t == typeof(Vector4))        return "vector4";
            if (t == typeof(Color))          return "color";
            if (t == typeof(Color32))        return "color32";
            if (t == typeof(Rect))           return "rect";
            if (t == typeof(Bounds))         return "bounds";
            if (t == typeof(Quaternion))     return "quaternion";
            if (t == typeof(LayerMask))      return "layermask";
            if (t == typeof(AnimationCurve)) return "curve";
            if (t.IsEnum && Enum.GetUnderlyingType(t) == typeof(int)) return "enum";
            return null;
        }

        /// <summary>
        /// True for field types Unity would serialize that wiring cannot restore,
        /// so the author is warned. Types Unity never serializes are skipped silently.
        /// </summary>
        private static bool UnitySerializesButUnsupported(Type t)
        {
            if (t.IsArray) return true;
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>)) return true;
            if (typeof(Delegate).IsAssignableFrom(t)) return false;           // never serialized by Unity
            bool unityNs = t.Namespace != null && t.Namespace.StartsWith("UnityEngine");
            if (t.IsGenericType) return unityNs;                               // e.g. UnityEvent<T>; not Dictionary<,>
            if (t.IsEnum) return true;                                         // non-int underlying type
            if (t.IsSerializable) return true;                                 // [Serializable] types, double, long ...
            return unityNs;                                                    // Gradient, Vector3Int, UnityEvent ...
        }

        private static string SerialiseValue(Type t, object v)
        {
            if (t == typeof(string)) return (string)v ?? "";
            if (v == null) return null;

            var inv = CultureInfo.InvariantCulture;
            if (t == typeof(float))     return ((float)v).ToString("R", inv);
            if (t == typeof(int))       return ((int)v).ToString(inv);
            if (t == typeof(bool))      return (bool)v ? "true" : "false";
            if (t.IsEnum)               return Convert.ToInt32(v, inv).ToString(inv);
            if (t == typeof(LayerMask)) return ((LayerMask)v).value.ToString(inv);

            // Structs and curves: the runner wraps the value as {"v": value},
            // so store exactly the inner JSON that JsonUtility produced.
            if (t == typeof(Vector2))        return Inner(JsonUtility.ToJson(new Vector2Wrapper    { v = (Vector2)v }));
            if (t == typeof(Vector3))        return Inner(JsonUtility.ToJson(new Vector3Wrapper    { v = (Vector3)v }));
            if (t == typeof(Vector4))        return Inner(JsonUtility.ToJson(new Vector4Wrapper    { v = (Vector4)v }));
            if (t == typeof(Color))          return Inner(JsonUtility.ToJson(new ColorWrapper      { v = (Color)v }));
            if (t == typeof(Color32))        return Inner(JsonUtility.ToJson(new Color32Wrapper    { v = (Color32)v }));
            if (t == typeof(Rect))           return Inner(JsonUtility.ToJson(new RectWrapper       { v = (Rect)v }));
            if (t == typeof(Bounds))         return Inner(JsonUtility.ToJson(new BoundsWrapper     { v = (Bounds)v }));
            if (t == typeof(Quaternion))     return Inner(JsonUtility.ToJson(new QuaternionWrapper { v = (Quaternion)v }));
            if (t == typeof(AnimationCurve)) return Inner(JsonUtility.ToJson(new CurveWrapper      { v = (AnimationCurve)v }));
            return null;
        }

        private static string Inner(string wrapped)
        {
            const string prefix = "{\"v\":";
            if (wrapped.StartsWith(prefix) && wrapped.EndsWith("}"))
                return wrapped.Substring(prefix.Length, wrapped.Length - prefix.Length - 1);
            throw new InvalidOperationException($"Unexpected JsonUtility output: {wrapped}");
        }

        [Serializable] private class Vector2Wrapper    { public Vector2    v; }
        [Serializable] private class Vector3Wrapper    { public Vector3    v; }
        [Serializable] private class Vector4Wrapper    { public Vector4    v; }
        [Serializable] private class ColorWrapper      { public Color      v; }
        [Serializable] private class Color32Wrapper    { public Color32    v; }
        [Serializable] private class RectWrapper       { public Rect       v; }
        [Serializable] private class BoundsWrapper     { public Bounds     v; }
        [Serializable] private class QuaternionWrapper { public Quaternion v; }
        [Serializable] private class CurveWrapper      { public AnimationCurve v = new AnimationCurve(); }

        private static string Pretty(Type t)
        {
            if (t.IsArray) return Pretty(t.GetElementType()) + "[]";
            if (!t.IsGenericType) return t.Name;
            string name = t.Name.Substring(0, t.Name.IndexOf('`'));
            return $"{name}<{string.Join(", ", t.GetGenericArguments().Select(Pretty))}>";
        }

        // ── Hierarchy helpers ─────────────────────────────────────

        private static IEnumerable<GameObject> AllGameObjects(GameObject root)
        {
            yield return root;
            foreach (Transform child in root.transform)
                foreach (var go in AllGameObjects(child.gameObject))
                    yield return go;
        }

        private static string HierarchyPath(GameObject go, GameObject root)
        {
            var parts = new List<string>();
            for (var t = go.transform; t != null; t = t.parent)
            {
                parts.Insert(0, t.name);
                if (t == root.transform) break;
            }
            return string.Join("/", parts);
        }

        // ── Report ────────────────────────────────────────────────

        private sealed class Report
        {
            private readonly string       _tag;
            private readonly List<string> _errors = new List<string>();
            private readonly List<string> _warns  = new List<string>();
            private readonly List<string> _infos  = new List<string>();

            public Report(string tag) { _tag = tag; }

            public bool HasErrors => _errors.Count > 0;
            public void Error(string m) => _errors.Add(m);
            public void Warn (string m) => _warns.Add(m);
            public void Info (string m) => _infos.Add(m);

            public void Flush()
            {
                foreach (var m in _errors) Debug.LogError  ($"{_tag} {m}");
                foreach (var m in _warns)  Debug.LogWarning($"{_tag} {m}");
                foreach (var m in _infos)  Debug.Log       ($"{_tag} {m}");
                _errors.Clear(); _warns.Clear(); _infos.Clear();
            }
        }
    }
}
#endif
