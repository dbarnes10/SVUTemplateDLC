// ============================================================
//  TemplateDLC.cs  —  TemplateDLC
// ============================================================
//  The plugin's entry point. The host loads TemplateDLC.dll,
//  finds this class (it must be the ONLY public IDLCPlugin in
//  the assembly), creates it and calls OnLoaded().
//
//  THREE HOOKS
//  ───────────
//  [1] INSTANTIATE — loads your content (everything under
//      TemplateContent in TemplateScene) from the bundle.
//      Required. Keep it.
//  [2] WIRING      — by the time InstantiatePrefab() returns, the
//      host has re-added every script in your content and restored
//      all their Inspector values and references. Nothing to call;
//      the example shows how to reach a component afterwards.
//  [3] WEB UI      — a commented-out float slider. Uncomment the
//      block marked [WEB-UI] to see it in the SVU web interface.
//
//  Id and Version must match manifest.json, and ContentAddress must
//  match the content root's name. "SVU DLC ▸ Plugin Settings…" keeps
//  all three in step — change them there, not here.
// ============================================================

using System.Collections.Generic;
using DLCFramework;
using UnityEngine;

public class TemplateDLC : IDLCPlugin, IDLCSyncable, svuSetterCallbackIF
{
    // ── IDLCPlugin identity ───────────────────────────────────
    public string Id      => "com.example.template";
    public string Version => "1.0.0";

    // Name of the content root in TemplateScene; also its bundle address.
    const string ContentAddress = "TemplateContent";

    // ── State ─────────────────────────────────────────────────
    IDLCContext _ctx;
    GameObject  _root;

    static bool   _hosted;
    static string _hostedDataPath;
    public static string DataPath => _hosted ? _hostedDataPath : Application.streamingAssetsPath;

    // Web UI parameters. Registered with the host after OnLoaded
    // returns and synced from the master node to every cluster node.
    readonly Dictionary<string, svuSettable> _params = new Dictionary<string, svuSettable>();

    // ── IDLCPlugin ────────────────────────────────────────────

    public void OnLoaded(IDLCContext context)
    {
        _ctx = context;
        _hosted         = true;
        _hostedDataPath = context.DataPath;

        // [1] INSTANTIATE ──────────────────────────────────────
        // Loads your content from this plugin's bundle at the
        // host-supplied spawn position.
        _root = context.InstantiatePrefab(ContentAddress, context.SpawnPosition);
        if (_root == null)
        {
            context.LogError($"Content '{ContentAddress}' not found in the bundle. " +
                             "Run 'SVU DLC ▸ Build and Export…' and redeploy the package.");
            return;
        }

        // [2] WIRING ───────────────────────────────────────────
        // Already complete: scripts are present and their fields
        // restored, and Awake/OnEnable have run. For example:
        //
        //   var behaviour = _root.GetComponentInChildren<TemplateBehaviour>(true);

        // [3] WEB UI ───────────────────────────────────────────
        // [WEB-UI] Uncomment to add a "Scale" slider to this plugin's
        // tab in the web UI. ApplyParams() below already handles it.
        //
        // _params["scale"] = new svuSettable("scale", "Scale", 1.0f, 0.1f, 3.0f, 0.05f, this)
        //                        .InGroup("Template");

        ApplyParams();
        context.Log($"Loaded. instance='{context.InstanceId}'");
    }

    public void OnUnloaded()
    {
        // The host destroys _root with the rest of the DLC hierarchy.
        // Release anything else here (events, statics, native resources).
        _root = null;
        _ctx  = null;
    }

    public bool OnBeforeReload() => true;

    // ── IDLCSyncable ──────────────────────────────────────────

    public Dictionary<string, svuSettable> State => _params;
    public float SyncIntervalSeconds => 0.05f; // 20 Hz

    // Client nodes: values have been updated silently by the sync driver.
    public void OnStateReceived(bool force = false) => ApplyParams();

    // ── svuSetterCallbackIF ───────────────────────────────────

    // Master node: a value changed via the web UI (or code).
    public void svuSetterCallback(bool force = false) => ApplyParams();

    // ── Apply parameters ──────────────────────────────────────

    // Single place where settable values reach the scene. Called on
    // load, on the master when a value changes, and on every client
    // after a sync, so all nodes stay consistent.
    void ApplyParams()
    {
        if (_root == null) return;

        if (_params.TryGetValue("scale", out var scale))
            _root.transform.localScale = Vector3.one * scale.Fval;
    }
}
