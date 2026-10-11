// ============================================================
//  DLCPluginSettings.cs  —  DLCTools (Editor)
// ============================================================
//  Finds the project's plugin and derives every name the tools
//  need from it. There is no per-plugin config script: the plugin
//  is the folder holding manifest.json next to an .asmdef named
//  after manifest.json's "assemblyName".
//
//  NAMING CONVENTION  (plugin name "Galaxy")
//    Folder / assembly / plugin class   GalaxyDLC
//    Content scene                      GalaxyDLC/GalaxyScene.unity
//    Content root GameObject + address  GalaxyContent
//    Built prefab                       GalaxyDLC/Build/GalaxyContent.prefab
//
//  One plugin per project.
// ============================================================

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DLCFramework;
using UnityEditor;

namespace DLCTools
{
    [Serializable]
    public sealed class DLCPluginSettings
    {
        /// <summary>Reverse-DNS package id — manifest.json "id".</summary>
        public string PackageId;

        /// <summary>Plugin assembly name — manifest.json "assemblyName", e.g. "GalaxyDLC".</summary>
        public string AssemblyName;

        /// <summary>Project-relative plugin folder, e.g. "Assets/GalaxyDLC".</summary>
        public string PluginFolder;

        // ── Derived names ─────────────────────────────────────────

        /// <summary>"Galaxy" for assembly "GalaxyDLC".</summary>
        public string PluginName => NameFromAssembly(AssemblyName);

        /// <summary>Content root GameObject name and Addressables address.</summary>
        public string ContentName      => PluginName + "Content";
        public string ScenePath        => $"{PluginFolder}/{PluginName}Scene.unity";
        public string ManifestJsonPath => PluginFolder + "/manifest.json";

        /// <summary>Generated output. Rebuilt on every build — never edit by hand.</summary>
        public string BuildFolder      => PluginFolder + "/Build";
        public string BuiltPrefabPath  => $"{BuildFolder}/{ContentName}.prefab";
        public string WireManifestPath => $"{BuildFolder}/{ContentName}_wire.json";

        /// <summary>One Addressables group per plugin, named after the package id.</summary>
        public string GroupName => PackageId;
        public string LogTag    => $"[{AssemblyName}]";

        public static string NameFromAssembly(string assemblyName)
            => assemblyName != null && assemblyName.EndsWith("DLC") && assemblyName.Length > 3
                ? assemblyName.Substring(0, assemblyName.Length - 3)
                : assemblyName;

        // ── Locate ────────────────────────────────────────────────

        /// <summary>
        /// Find the project's single plugin. Returns false with a readable
        /// error if there is none, more than one, or its files are inconsistent.
        /// </summary>
        public static bool TryLocate(out DLCPluginSettings settings, out string error)
        {
            settings = null;
            error    = null;
            var found = new List<DLCPluginSettings>();

            foreach (string guid in AssetDatabase.FindAssets("manifest t:TextAsset", new[] { "Assets" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileName(path) != "manifest.json") continue;

                string folder = Path.GetDirectoryName(path)?.Replace('\\', '/');
                if (string.IsNullOrEmpty(folder) || folder.EndsWith("/Build")) continue;

                DLCManifest m;
                try   { m = DLCManifest.FromJson(File.ReadAllText(path)); }
                catch { continue; }
                if (m == null || string.IsNullOrEmpty(m.id) || string.IsNullOrEmpty(m.assemblyName)) continue;
                if (!File.Exists($"{folder}/{m.assemblyName}.asmdef")) continue;

                found.Add(new DLCPluginSettings
                {
                    PackageId    = m.id,
                    AssemblyName = m.assemblyName,
                    PluginFolder = folder
                });
            }

            if (found.Count == 0)
            {
                error = "No plugin found. A plugin is a folder containing manifest.json and an .asmdef " +
                        "named after manifest.json's \"assemblyName\".";
                return false;
            }
            if (found.Count > 1)
            {
                error = $"This project contains {found.Count} plugins ({string.Join(", ", found.ConvertAll(f => f.PluginFolder))}). " +
                        "The SVU DLC tools support one plugin per project.";
                return false;
            }

            settings = found[0];
            return true;
        }

        // ── Plugin class Id / Version ─────────────────────────────
        // IDLCPlugin.Id and .Version must equal manifest.json's "id" and
        // "version". These helpers read and rewrite the two string literals
        //   public string Id      => "...";
        //   public string Version => "...";
        // in the plugin class, so Plugin Settings can keep them in step.

        private static readonly Regex IdLiteral =
            new Regex(@"(public\s+string\s+Id\s*=>\s*)""[^""]*""");
        private static readonly Regex VersionLiteral =
            new Regex(@"(public\s+string\s+Version\s*=>\s*)""[^""]*""");

        /// <summary>The .cs file declaring the IDLCPlugin class, or null if not exactly one.</summary>
        public string FindPluginClassFile()
        {
            var hits = Directory.GetFiles(PluginFolder, "*.cs", SearchOption.AllDirectories)
                                .Where(f => Regex.IsMatch(File.ReadAllText(f), @"class\s+\w+\s*:[^{]*\bIDLCPlugin\b"))
                                .ToList();
            return hits.Count == 1 ? hits[0].Replace('\\', '/') : null;
        }

        /// <summary>Reads the Id and Version literals. Null where not found.</summary>
        public void ReadPluginClassLiterals(out string id, out string version)
        {
            id = version = null;
            string file = FindPluginClassFile();
            if (file == null) return;
            string text = File.ReadAllText(file);
            var mi = IdLiteral.Match(text);
            var mv = VersionLiteral.Match(text);
            if (mi.Success) id      = mi.Value.Substring(mi.Groups[1].Value.Length).Trim('"');
            if (mv.Success) version = mv.Value.Substring(mv.Groups[1].Value.Length).Trim('"');
        }

        /// <summary>Rewrites the Id and Version literals. Returns false if either wasn't found.</summary>
        public bool WritePluginClassLiterals(string id, string version)
        {
            string file = FindPluginClassFile();
            if (file == null) return false;
            string text = File.ReadAllText(file);
            if (!IdLiteral.IsMatch(text) || !VersionLiteral.IsMatch(text)) return false;
            string updated = IdLiteral.Replace(text, m => m.Groups[1].Value + "\"" + id + "\"");
            updated        = VersionLiteral.Replace(updated, m => m.Groups[1].Value + "\"" + version + "\"");
            if (updated != text) File.WriteAllText(file, updated);
            return true;
        }
    }
}
#endif
