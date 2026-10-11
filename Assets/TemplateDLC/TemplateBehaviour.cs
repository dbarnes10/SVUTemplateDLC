// ============================================================
//  TemplateBehaviour.cs  —  TemplateDLC
// ============================================================
//  Example script on TemplateContent in TemplateScene. It exists to
//  show — and let you check — that wiring works. Replace or delete it.
//
//  Write plugin scripts as ordinary MonoBehaviours and set them
//  up in the Inspector as usual. They must live inside the
//  TemplateDLC folder so they compile into TemplateDLC.dll.
//  The bundle build strips them from your content, and the host
//  puts them back at load time with these fields restored:
//
//    tint           — a value field (Color)
//    targetRenderer — a reference to a component in your content
//
//  WIRING CHECK — how the cube looks on the wall:
//    orange  both fields restored: wiring works.
//    white   targetRenderer restored, tint NOT (white is the code
//            default below; orange is the value saved in the scene).
//    blue    this script wasn't re-added or targetRenderer wasn't
//            restored — the material's own colour shows.
//  In the editor the cube is blue until you press Play.
//  Check the host Console for [DLCWire] messages if it isn't orange.
//
//  Arrays, Lists and nested serializable classes are NOT restored
//  (the build warns about them). Initialise those in code.
// ============================================================

using UnityEngine;

public class TemplateBehaviour : MonoBehaviour
{
    // Default is deliberately different from the value set in the scene
    // (orange), so a failed value restore shows up as a white cube.
    [Tooltip("Colour applied to Target Renderer at start.")]
    public Color tint = Color.white;

    [Tooltip("Renderer to tint. Assigned to Model under TemplateContent.")]
    public Renderer targetRenderer;

    void Start()
    {
        if (targetRenderer == null) return;

        // A property block tints this instance without creating a
        // new material. _BaseColor is the URP Lit colour property.
        var block = new MaterialPropertyBlock();
        targetRenderer.GetPropertyBlock(block);
        block.SetColor("_BaseColor", tint);
        targetRenderer.SetPropertyBlock(block);
    }
}
