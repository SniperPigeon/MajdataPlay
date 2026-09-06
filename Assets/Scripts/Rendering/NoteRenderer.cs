using UnityEngine;

#nullable enable
namespace MajdataPlay.Rendering
{
    /// <summary>
    /// Borrows preloaded skin resources. Flips are always ignored; pooled note
    /// replacement, disable and destruction do not release the skin's resources.
    /// </summary>
    [AddComponentMenu("Rendering/Note Renderer")]
    public sealed class NoteRenderer : RawSpriteRenderer
    {
        int _resourceVersion = -1;

        protected override void OnPreLateUpdate()
        {
            if (_resourceVersion != NoteSpriteResources.Version)
            {
                _resourceVersion = NoteSpriteResources.Version;
                MarkAllDirty();
            }
            base.OnPreLateUpdate();
        }

        protected override Mesh? AcquireMesh(Sprite? sprite, bool flipX, bool flipY)
        {
#if UNITY_EDITOR
            if (!Application.isPlaying)
                return base.AcquireMesh(sprite, false, false);
#endif
            return NoteSpriteResources.GetMesh(sprite);
        }

        protected override Material AcquireMaterial(Material? source, Texture? texture)
        {
#if UNITY_EDITOR
            if (!Application.isPlaying)
                return base.AcquireMaterial(source, texture);
#endif
            return NoteSpriteResources.GetMaterial(source, texture)!;
        }
    }
}
