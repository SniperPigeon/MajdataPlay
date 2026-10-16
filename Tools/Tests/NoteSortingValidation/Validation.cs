#nullable enable
#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEditor;
using Sorting = MajdataPlay.Scenes.Game.Utils.NoteHelper.Sorting;
using Object = UnityEngine.Object;

namespace NoteSortingValidation
{
    /// <summary>
    /// Checks production note depth helpers with Unity APIs and serialized gameplay assets.
    /// </summary>
    public static class Validation
    {
        /// <summary>
        /// Runs helper, asset and desktop rendering regressions.
        /// </summary>
        /// <exception cref="InvalidOperationException">A regression check fails.</exception>
        public static void Run()
        {
            ValidateDepthOrder();
            ValidateTransforms();
            ValidateNestedRenderers();
            ValidateRendering();
            Debug.Log("NOTE_SORTING_RENDERING_PASSED");
            ValidateAssets();
            Debug.Log("NOTE_SORTING_VALIDATION_PASSED");
        }

        /// <summary>
        /// Runs helper and asset regressions without a graphics device.
        /// </summary>
        /// <exception cref="InvalidOperationException">A regression check fails.</exception>
        public static void RunWithoutRendering()
        {
            ValidateDepthOrder();
            ValidateTransforms();
            ValidateNestedRenderers();
            ValidateAssets();
            Debug.Log("NOTE_SORTING_VALIDATION_PASSED");
        }

        /// <summary>
        /// Checks category isolation, long-chart orders and component priorities.
        /// </summary>
        /// <exception cref="InvalidOperationException">A depth check fails.</exception>
        private static void ValidateDepthOrder()
        {
            var layers = (Sorting.NoteLayer[])Enum.GetValues(typeof(Sorting.NoteLayer));
            var orders = new[] { int.MinValue, -1000000, -65536, -32769, -32768, -1, 0, 1, 32767, 32768, 32769, 65536, 1000000, int.MaxValue };
            for (var i = 0; i < layers.Length; i++)
            {
                var layer = layers[i];
                Near(Sorting.GetZIndex(layer), -i * 0.4f, $"Category {layer}");
                for (var j = 0; j < orders.Length; j++)
                {
                    var z = Sorting.GetZIndex(layer, orders[j]);
                    Check(!float.IsNaN(z) && !float.IsInfinity(z), $"Finite depth {layer}/{orders[j]}");
                    Check(Math.Abs(z + i * 0.4f) <= 0.150001f, $"Bounded depth {layer}/{orders[j]}");
                    if (j > 0)
                    {
                        Check(z < Sorting.GetZIndex(layer, orders[j - 1]), $"Long-chart priority {layer}/{orders[j]}");
                    }
                }
                if (i > 0)
                {
                    Check(Sorting.GetZIndex(layer, int.MinValue) < Sorting.GetZIndex(layers[i - 1], int.MaxValue), $"Disjoint categories {layer}");
                }
            }

            foreach (var order in new[] { -65536, -32769, -32768, -3, 0, 32767, 32768, 65536 })
            {
                var body = Sorting.GetZIndex(Sorting.NoteLayer.Tap, order - 1);
                var end = Sorting.GetZIndex(Sorting.NoteLayer.Tap, order - 2);
                var ex = Sorting.GetZIndex(Sorting.NoteLayer.Tap, order);
                Check(ex < body && body < end, $"Hold EX/body/end priority {order}");
                var justBorder = Sorting.GetZIndex(Sorting.NoteLayer.Touch, order - 1);
                var point = Sorting.GetZIndex(Sorting.NoteLayer.Touch, order - 2);
                Check(justBorder < point, $"Touch just-border/point priority {order}");
                var previous = point;
                for (var fan = 0; fan < 4; fan++)
                {
                    var z = Sorting.GetZIndex(Sorting.NoteLayer.Touch, order - (3 + fan));
                    Check(previous < z, $"Touch fan {fan} priority {order}");
                    previous = z;
                }
            }
            Debug.Log("NOTE_SORTING_DEPTH_PASSED: all categories, signed 16-bit boundaries, int32 extremes and component priorities");
        }

        /// <summary>
        /// Checks local depth assignment and movement under transformed parents.
        /// </summary>
        /// <exception cref="InvalidOperationException">A renderer or transform check fails.</exception>
        private static void ValidateTransforms()
        {
            var parent = new GameObject("Sorting parent");
            var alternateParent = new GameObject("Alternate sorting parent");
            var child = new GameObject("Sorting child");
            try
            {
                parent.transform.position = new Vector3(5, -4, 12);
                parent.transform.rotation = Quaternion.Euler(0, 0, 37);
                parent.transform.localScale = new Vector3(2, 3, 2);
                child.transform.SetParent(parent.transform, false);
                child.transform.localPosition = new Vector3(1, 2, 3);
                var renderer = child.AddComponent<SpriteRenderer>();
                renderer.sortingLayerName = "Tap";
                renderer.sortingOrder = 123;
                renderer.spriteSortPoint = SpriteSortPoint.Center;
                var original = child.transform.localPosition;
                Sorting.Apply(renderer, Sorting.NoteLayer.Touch, -32769);
                Check(renderer.sortingLayerID == 0 && renderer.sortingLayerName == "Default", "Apply resets sorting layer");
                Check(renderer.sortingOrder == 0 && renderer.spriteSortPoint == SpriteSortPoint.Pivot, "Apply resets order and pivot");
                Near(child.transform.localPosition.x, original.x, "Apply preserves local X");
                Near(child.transform.localPosition.y, original.y, "Apply preserves local Y");
                var z = Sorting.GetZIndex(Sorting.NoteLayer.Touch, -32769);
                Near(child.transform.localPosition.z, z, "Apply sets local Z");
                Near(child.transform.position.z, 12 + 2 * z, "Apply inherits parent depth and Z scale");
                var worldZ = child.transform.position.z;
                for (var frame = 0; frame < 120; frame++)
                {
                    var angle = frame * Mathf.PI / 60;
                    Sorting.SetPosition(child.transform, new Vector3(Mathf.Cos(angle) * 4, Mathf.Sin(angle) * 4, 99));
                    Near(child.transform.position.z, worldZ, "World XY movement preserves world Z");
                    Near(child.transform.localPosition.z, z, "Planar world XY movement preserves local Z");
                }
                Sorting.SetLocalPosition(child.transform, new Vector3(-2, 7, 99));
                Near(child.transform.localPosition.x, -2, "SetLocalPosition X");
                Near(child.transform.localPosition.y, 7, "SetLocalPosition Y");
                Near(child.transform.localPosition.z, z, "SetLocalPosition preserves local Z");
                original = child.transform.localPosition;
                Sorting.SetLocalZPosition(child.transform, -7);
                Near(child.transform.localPosition.x, original.x, "SetLocalZPosition preserves local X");
                Near(child.transform.localPosition.y, original.y, "SetLocalZPosition preserves local Y");
                Near(child.transform.localPosition.z, -7, "SetLocalZPosition sets local Z");
                Near(child.transform.position.z, -2, "SetLocalZPosition inherits parent depth and Z scale");
                Sorting.Apply(renderer, Sorting.NoteLayer.Slides, 32769);
                z = Sorting.GetZIndex(Sorting.NoteLayer.Slides, 32769);
                Near(child.transform.localPosition.z, z, "Pooled renderer changes local category");
                worldZ = child.transform.position.z;
                foreach (var scale in new[] { 0f, 0.5f, 1f, 3f })
                {
                    parent.transform.localScale = new Vector3(scale, scale, 2);
                    Sorting.SetLocalPosition(child.transform, new Vector3(scale, -scale, 0));
                    Near(child.transform.localPosition.z, z, $"XY scale {scale} preserves local depth");
                    Near(child.transform.position.z, worldZ, $"XY scale {scale} preserves inherited world depth");
                }
                parent.transform.position += new Vector3(0, 0, 5);
                Near(child.transform.localPosition.z, z, "Parent movement preserves local index");
                Near(child.transform.position.z, worldZ + 5, "Child inherits parent depth movement");

                alternateParent.transform.position = new Vector3(1, 8, -7);
                alternateParent.transform.rotation = Quaternion.Euler(0, 0, -12);
                alternateParent.transform.localScale = new Vector3(0.5f, 2, 3);
                original = child.transform.localPosition;
                child.transform.SetParent(alternateParent.transform, false);
                Near(child.transform.localPosition.x, original.x, "Local reparent preserves local X");
                Near(child.transform.localPosition.y, original.y, "Local reparent preserves local Y");
                Near(child.transform.localPosition.z, z, "Local reparent preserves assigned index");
                Near(child.transform.position.z, -7 + 3 * z, "Local reparent inherits new parent depth and Z scale");

                alternateParent.transform.rotation = Quaternion.Euler(20, 30, -12);
                original = child.transform.localPosition;
                Sorting.SetLocalZPosition(child.transform, -4);
                Near(child.transform.localPosition.x, original.x, "Tilted parent preserves local X");
                Near(child.transform.localPosition.y, original.y, "Tilted parent preserves local Y");
                Near(child.transform.localPosition.z, -4, "Tilted parent sets local Z");
            }
            finally
            {
                Object.DestroyImmediate(child);
                Object.DestroyImmediate(alternateParent);
                Object.DestroyImmediate(parent);
            }
            Debug.Log("NOTE_SORTING_TRANSFORMS_PASSED: local indices, pooled reset, movement and transformed parents");
        }

        /// <summary>
        /// Checks nested Tap and Hold components use relative local depth without duplicating the body index.
        /// </summary>
        /// <exception cref="InvalidOperationException">A nested renderer priority check fails.</exception>
        private static void ValidateNestedRenderers()
        {
            var parent = new GameObject("Nested sorting parent");
            var bodyObject = new GameObject("Tap or Hold body");
            var exObject = new GameObject("Tap or Hold EX");
            var endObject = new GameObject("Hold end");
            try
            {
                parent.transform.position = new Vector3(5, -4, 12);
                parent.transform.rotation = Quaternion.Euler(0, 0, 37);
                parent.transform.localScale = new Vector3(2, 3, 2);
                bodyObject.transform.SetParent(parent.transform, false);
                bodyObject.transform.localScale = new Vector3(1.5f, 2, 1);
                exObject.transform.SetParent(bodyObject.transform, false);
                endObject.transform.SetParent(bodyObject.transform, false);
                var body = bodyObject.AddComponent<SpriteRenderer>();
                var ex = exObject.AddComponent<SpriteRenderer>();
                var end = endObject.AddComponent<SpriteRenderer>();
                foreach (var order in new[] { -32768, 0, 32768 })
                {
                    var bodyZ = Sorting.GetZIndex(Sorting.NoteLayer.Tap, order - 1);
                    var exZ = Sorting.GetZIndex(Sorting.NoteLayer.Tap, order);
                    var endZ = Sorting.GetZIndex(Sorting.NoteLayer.Tap, order - 2);
                    Sorting.Apply(body, Sorting.NoteLayer.Tap, order - 1);
                    Sorting.Apply(ex, Sorting.NoteLayer.Tap, order, bodyZ);
                    Sorting.Apply(end, Sorting.NoteLayer.Tap, order - 2, bodyZ);
                    Near(body.transform.localPosition.z, bodyZ, $"Nested body local index {order}");
                    Near(ex.transform.localPosition.z, exZ - bodyZ, $"Nested EX relative local index {order}");
                    Near(end.transform.localPosition.z, endZ - bodyZ, $"Nested end relative local index {order}");
                    Check(ex.transform.localPosition.z < 0 && end.transform.localPosition.z > 0, $"Nested EX/body/end local priority {order}");
                    Near(ex.transform.position.z, 12 + 2 * exZ, $"Nested EX inherits one category index {order}");
                    Near(end.transform.position.z, 12 + 2 * endZ, $"Nested end inherits one category index {order}");
                    Check(ex.transform.position.z < body.transform.position.z && body.transform.position.z < end.transform.position.z,
                        $"Nested EX/body/end world priority {order}");
                }
                var bodyLocalZ = body.transform.localPosition.z;
                var exLocalZ = ex.transform.localPosition.z;
                var endLocalZ = end.transform.localPosition.z;
                var bodyWorldZ = body.transform.position.z;
                var exWorldZ = ex.transform.position.z;
                var endWorldZ = end.transform.position.z;
                parent.transform.position += new Vector3(0, 0, 5);
                Near(body.transform.localPosition.z, bodyLocalZ, "Parent movement preserves nested body local index");
                Near(ex.transform.localPosition.z, exLocalZ, "Parent movement preserves nested EX local index");
                Near(end.transform.localPosition.z, endLocalZ, "Parent movement preserves nested end local index");
                Near(body.transform.position.z, bodyWorldZ + 5, "Nested body inherits parent depth movement");
                Near(ex.transform.position.z, exWorldZ + 5, "Nested EX inherits parent depth movement");
                Near(end.transform.position.z, endWorldZ + 5, "Nested end inherits parent depth movement");
            }
            finally
            {
                Object.DestroyImmediate(endObject);
                Object.DestroyImmediate(exObject);
                Object.DestroyImmediate(bodyObject);
                Object.DestroyImmediate(parent);
            }
            Debug.Log("NOTE_SORTING_NESTED_PASSED: Tap and Hold relative component depths preserve EX/body/end priority");
        }

        /// <summary>
        /// Scans complete SpriteRenderer documents and variant/scene sorting overrides.
        /// </summary>
        /// <exception cref="InvalidOperationException">An asset check fails.</exception>
        private static void ValidateAssets()
        {
            var root = Path.GetFullPath(Path.Combine(Application.dataPath, "../../.."));
            var files = Directory.GetFiles(Path.Combine(root, "Assets/Prefabs/Game"), "*.prefab", SearchOption.AllDirectories);
            var prefabPaths = new Dictionary<string, string>();
            foreach (var meta in Directory.GetFiles(Path.Combine(root, "Assets"), "*.prefab.meta", SearchOption.AllDirectories))
            {
                var guid = Regex.Match(File.ReadAllText(meta), @"(?m)^guid: ([a-f0-9]{32})\r?$");
                if (guid.Success)
                {
                    prefabPaths.Add(guid.Groups[1].Value, meta.Substring(0, meta.Length - ".meta".Length));
                }
            }
            var renderers = 0;
            var overrides = 0;
            foreach (var path in files)
            {
                ValidateFile(path);
            }
            ValidateFile(Path.Combine(root, "Assets/Prefabs/UI/TouchFeedback.prefab"));
            ValidateFile(Path.Combine(root, "Assets/Scenes/Game.unity"));
            var holdEffect = File.ReadAllText(Path.Combine(root, "Assets/Prefabs/Game/Hold_Effect.prefab"));
            var particle = Regex.Match(holdEffect, @"(?ms)^--- !u!199 &[^\r\n]+\r?\nParticleSystemRenderer:\r?\n(?<body>.*?)(?=^--- !u!|\z)");
            Check(particle.Success, "Hold effect particle renderer exists");
            Check(ReadInteger(particle.Groups["body"].Value, "m_SortingLayerID") == 0, "Hold effect particle uses Default");
            Check(ReadInteger(particle.Groups["body"].Value, "m_SortingOrder") == 0, "Hold effect particle uses zero order");
            Check(ReadInteger(particle.Groups["body"].Value, "m_SortingFudge") == 0, "Hold effect particle has no depth bias");
            var touchHold = File.ReadAllText(Path.Combine(root, "Assets/Prefabs/Game/TouchHold.prefab"));
            Check(Regex.IsMatch(touchHold, @"(?m)^  _sortingLayerName: Default\r?$"), "TouchHold RawSprite border uses Default");
            Check(ReadInteger(touchHold, "_sortingOrder") == 0, "TouchHold RawSprite border uses zero order");
            var game = File.ReadAllText(Path.Combine(root, "Assets/Scenes/Game.unity"));
            foreach (var canvasId in new[] { 1076127510, 1353679705, 1643501205 })
            {
                var canvas = Regex.Match(game, @"(?ms)^--- !u!223 &" + canvasId + @"\r?\nCanvas:\r?\n(?<body>.*?)(?=^--- !u!|\z)");
                Check(canvas.Success, $"World canvas {canvasId} exists");
                Check(ReadInteger(canvas.Groups["body"].Value, "m_SortingLayerID") == 0, $"World canvas {canvasId} uses Default");
                Check(ReadInteger(canvas.Groups["body"].Value, "m_SortingOrder") == 0, $"World canvas {canvasId} uses zero order");
            }
            Check(renderers > 100, "Gameplay SpriteRenderer scan is populated");
            Check(overrides > 0, "Prefab variant and scene override scan is populated");
            Debug.Log($"NOTE_SORTING_ASSETS_PASSED: {renderers} SpriteRenderers, {overrides} sorting overrides, {files.Length + 2} files");
            var animations = 0;
            foreach (var directory in new[] { "NoteEffects", "TouchEffects", "OutlineEffect" })
            {
                foreach (var path in Directory.GetFiles(Path.Combine(root, "Assets/Animation", directory), "*.anim", SearchOption.AllDirectories))
                {
                    ValidateAnimation(path);
                }
            }
            ValidateAnimation(Path.Combine(root, "Assets/Animation/UI/TouchFeedbackAnim.anim"));
            Check(animations > 20, "Gameplay animation scan is populated");
            Debug.Log($"NOTE_SORTING_ANIMATIONS_PASSED: {animations} clips preserve local Z, Z scale and renderer sorting");

            /// <summary>Rejects animation bindings that restore a renderer sorting property.</summary>
            /// <param name="path">The animation path.</param>
            /// <exception cref="InvalidOperationException">A sorting-property curve is present.</exception>
            void ValidateAnimation(string path)
            {
                var source = File.ReadAllText(path);
                Check(!Regex.IsMatch(source, @"attribute: m_(?:SortingLayerID|SortingOrder|SortingLayer|SpriteSortPoint)\r?\n"), $"No renderer sorting animation in {path}");
                var relative = path.Substring(Path.Combine(root, "Assets/Animation").Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>("Assets/Animation/" + relative.Replace('\\', '/'));
                Check(clip != null, $"Imported animation {path}");
                var bindings = new List<EditorCurveBinding>();
                var animationRoot = new GameObject("Animation depth root");
                animationRoot.transform.position = new Vector3(0, 0, -6.4f);
                try
                {
                    foreach (var binding in AnimationUtility.GetCurveBindings(clip))
                    {
                        if (binding.type != typeof(Transform) ||
                            (binding.propertyName != "m_LocalPosition.z" && binding.propertyName != "m_LocalScale.z"))
                        {
                            continue;
                        }
                        bindings.Add(binding);
                        var parent = animationRoot.transform;
                        foreach (var segment in binding.path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            var child = parent.Find(segment);
                            if (child == null)
                            {
                                child = new GameObject(segment).transform;
                                child.SetParent(parent, false);
                            }
                            parent = child;
                        }
                        var curve = AnimationUtility.GetEditorCurve(clip, binding);
                        var expected = binding.propertyName == "m_LocalPosition.z" ? 0f : 1f;
                        for (var frame = 0; frame <= 30; frame++)
                        {
                            Near(curve.Evaluate(clip.length * frame / 30), expected, $"Editor curve {path}, {binding.path}/{binding.propertyName}");
                        }
                    }
                    for (var frame = 0; frame <= 30; frame++)
                    {
                        clip.SampleAnimation(animationRoot, clip.length * frame / 30);
                        Near(animationRoot.transform.position.z, -6.4f, $"Animation {path} preserves mapped root depth");
                        foreach (var binding in bindings)
                        {
                            var transform = binding.path.Length == 0 ? animationRoot.transform : animationRoot.transform.Find(binding.path);
                            var actual = binding.propertyName == "m_LocalPosition.z" ? transform.localPosition.z : transform.localScale.z;
                            var expected = binding.propertyName == "m_LocalPosition.z" ? 0f : 1f;
                            Near(actual, expected, $"Sampled animation {path}, {binding.path}/{binding.propertyName}");
                        }
                    }
                }
                finally
                {
                    Object.DestroyImmediate(animationRoot);
                }
                animations++;
            }

            /// <summary>Checks one serialized scene or prefab without importing gameplay scripts.</summary>
            /// <param name="path">The scene or prefab path.</param>
            /// <exception cref="InvalidOperationException">A serialized sorting value is incorrect.</exception>
            void ValidateFile(string path)
            {
                var source = File.ReadAllText(path);
                foreach (Match document in Regex.Matches(source, @"(?ms)^--- !u!212 &[^\r\n]+\r?\nSpriteRenderer:\r?\n(?<body>.*?)(?=^--- !u!|\z)"))
                {
                    var body = document.Groups["body"].Value;
                    if (!body.Contains("m_SortingLayerID:"))
                    {
                        Check(document.Value.Contains("stripped"), $"Unexpected incomplete SpriteRenderer in {path}");
                        continue;
                    }
                    Check(ReadInteger(body, "m_SortingLayerID") == 0, $"Default layer in {path}");
                    Check(ReadInteger(body, "m_SortingOrder") == 0, $"Zero renderer order in {path}");
                    Check(ReadInteger(body, "m_SpriteSortPoint") == 1, $"Pivot sort point in {path}");
                    renderers++;
                }
                foreach (Match entry in Regex.Matches(source, @"target: \{fileID: (?<id>-?\d+), guid: (?<guid>[a-f0-9]{32}), type: 3\}\r?\n\s+propertyPath: m_(?<property>SortingLayerID|SortingOrder|SpriteSortPoint)\r?\n\s+value: (?<value>-?\d+)"))
                {
                    var guid = entry.Groups["guid"].Value;
                    Check(prefabPaths.TryGetValue(guid, out var prefab), $"Prefab override source {guid} exists");
                    var prefabSource = File.ReadAllText(prefab);
                    if (!Regex.IsMatch(prefabSource, @"(?m)^--- !u!212 &" + entry.Groups["id"].Value + @"(?: stripped)?\r?$"))
                    {
                        continue;
                    }
                    var expected = entry.Groups["property"].Value == "SpriteSortPoint" ? 1 : 0;
                    Check(int.Parse(entry.Groups["value"].Value, CultureInfo.InvariantCulture) == expected, $"Default sorting override in {path}");
                    overrides++;
                }
            }
        }

        /// <summary>
        /// Verifies that Z takes priority over XY distance and creation order in real rendering.
        /// </summary>
        /// <exception cref="InvalidOperationException">Rendering or pixel checks fail.</exception>
        private static void ValidateRendering()
        {
            Check(SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null, "A graphics device is required for the rendering check");
            var cameraObject = new GameObject("Sorting camera");
            var rearObject = new GameObject("Rear red sprite");
            var frontObject = new GameObject("Front green sprite");
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            var target = new RenderTexture(64, 64, 16, RenderTextureFormat.ARGB32);
            var readback = new Texture2D(64, 64, TextureFormat.RGBA32, false);
            Sprite? sprite = null;
            var previousTarget = RenderTexture.active;
            try
            {
                texture.SetPixels(new[] { Color.white, Color.white, Color.white, Color.white });
                texture.Apply();
                sprite = Sprite.Create(texture, new Rect(0, 0, 2, 2), Vector2.one * 0.5f, 1);
                var rear = rearObject.AddComponent<SpriteRenderer>();
                var front = frontObject.AddComponent<SpriteRenderer>();
                rear.sprite = sprite;
                front.sprite = sprite;
                rear.color = Color.red;
                front.color = Color.green;
                rear.transform.localScale = Vector3.one * 10;
                front.transform.localScale = Vector3.one * 10;
                rear.transform.position = new Vector3(0, 0, 0);
                front.transform.position = new Vector3(7, 7, 0);
                var camera = cameraObject.AddComponent<Camera>();
                camera.transform.position = new Vector3(0, 0, -10);
                camera.orthographic = true;
                camera.orthographicSize = 10;
                camera.nearClipPlane = 0.1f;
                camera.farClipPlane = 30;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.black;
                camera.transparencySortMode = TransparencySortMode.CustomAxis;
                camera.transparencySortAxis = Vector3.forward;
                camera.targetTexture = target;
                foreach (var reverse in new[] { false, true })
                {
                    Sorting.Apply(rear, reverse ? Sorting.NoteLayer.Touch : Sorting.NoteLayer.Tap);
                    Sorting.Apply(front, reverse ? Sorting.NoteLayer.Tap : Sorting.NoteLayer.Touch);
                    camera.Render();
                    RenderTexture.active = target;
                    readback.ReadPixels(new Rect(0, 0, 64, 64), 0, 0);
                    readback.Apply();
                    var sample = readback.GetPixel(40, 40);
                    Check(reverse ? sample.r > 0.9f && sample.g < 0.1f : sample.g > 0.9f && sample.r < 0.1f,
                        $"Z wins at nonzero XY with reversed category priority ({reverse}): {sample}");
                }
                Check((front.transform.position - camera.transform.position).sqrMagnitude > (rear.transform.position - camera.transform.position).sqrMagnitude,
                    "The XY fixture opposes distance sorting");
                Debug.Log($"NOTE_SORTING_RENDER_DEVICE: {SystemInfo.graphicsDeviceType}, {SystemInfo.graphicsDeviceName}");
            }
            finally
            {
                RenderTexture.active = previousTarget;
                Object.DestroyImmediate(cameraObject);
                Object.DestroyImmediate(rearObject);
                Object.DestroyImmediate(frontObject);
                Object.DestroyImmediate(sprite);
                Object.DestroyImmediate(texture);
                Object.DestroyImmediate(readback);
                Object.DestroyImmediate(target);
            }
        }

        /// <summary>
        /// Reads a required integer field from a serialized component.
        /// </summary>
        /// <param name="source">The component text.</param>
        /// <param name="name">The serialized field name.</param>
        /// <returns>The required integer value.</returns>
        /// <exception cref="InvalidOperationException">The field is missing.</exception>
        private static int ReadInteger(string source, string name)
        {
            var match = Regex.Match(source, @"(?m)^  " + name + @": (-?\d+)\r?$");
            Check(match.Success, $"Missing serialized field {name}");
            return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Checks a world or local coordinate within Unity transform precision.
        /// </summary>
        /// <param name="actual">The observed value.</param>
        /// <param name="expected">The expected value.</param>
        /// <param name="context">The check description.</param>
        /// <exception cref="InvalidOperationException">The values differ.</exception>
        private static void Near(float actual, float expected, string context)
        {
            Check(Mathf.Abs(actual - expected) < 0.00001f, $"{context}: expected {expected}, got {actual}");
        }

        /// <summary>
        /// Rejects an unsatisfied regression condition.
        /// </summary>
        /// <param name="condition">Whether the check passed.</param>
        /// <param name="context">The check description.</param>
        /// <exception cref="InvalidOperationException">The condition is false.</exception>
        private static void Check(bool condition, string context)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Note sorting validation failed: " + context);
            }
        }
    }
}
#endif
