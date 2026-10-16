using MajdataPlay.Scenes.Game.Notes.Slide;
using MajdataPlay.IO;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

#nullable enable
namespace MajdataPlay.Scenes.Game.Utils
{
    internal static class NoteHelper
    {
        /// <summary>
        /// Converts the gameplay sprite stack to world-space Z coordinates.
        /// Smaller Z coordinates are drawn closer to the gameplay camera.
        /// </summary>
        internal static class Sorting
        {
            /// <summary>
            /// Separates adjacent gameplay groups within the camera's visible depth range.
            /// </summary>
            private const float LayerSpacing = 0.4f;

            /// <summary>
            /// Limits order offsets to less than half the distance between groups.
            /// </summary>
            private const double OrderDepth = 0.15d;

            /// <summary>
            /// Spreads the original signed 16-bit order range around each group's center.
            /// </summary>
            private const double OrderScale = 32768d;

            /// <summary>
            /// Identifies the back-to-front groups of gameplay sprites.
            /// </summary>
            internal enum NoteLayer
            {
                /// <summary>Slide and Wifi tracks.</summary>
                Slides,
                /// <summary>Stars following slide paths.</summary>
                SlideStars,
                /// <summary>Slide judgment results.</summary>
                SlideOK,
                /// <summary>Tap guide lines and each lines.</summary>
                TapLines,
                /// <summary>Tap, Hold and slide-head sprites.</summary>
                Tap,
                /// <summary>TouchHold progress borders.</summary>
                TouchHoldBorder,
                /// <summary>Touch, TouchHold and multiple-touch borders.</summary>
                Touch
            }

            /// <summary>
            /// Gets a sprite's Z index without the signed 16-bit renderer order limit.
            /// Each group occupies a disjoint, bounded depth range in front of the camera.
            /// </summary>
            /// <param name="layer">The sprite's gameplay group.</param>
            /// <param name="sortOrder">The front-to-back priority; larger values appear in front.</param>
            /// <returns>The world-space Z coordinate for the sprite.</returns>
            internal static float GetZIndex(NoteLayer layer, int sortOrder = 0)
            {
                var offset = OrderDepth * sortOrder / (OrderScale + Math.Abs((double)sortOrder));
                return (-(int)layer * LayerSpacing) - (float)offset;
            }

            /// <summary>
            /// Assigns a sprite to Default with zero order and positions its pivot by depth.
            /// </summary>
            /// <param name="renderer">The sprite renderer to configure.</param>
            /// <param name="layer">The sprite's gameplay group.</param>
            /// <param name="sortOrder">The priority within the gameplay group.</param>
            internal static void Apply(SpriteRenderer renderer, NoteLayer layer, int sortOrder = 0)
            {
                renderer.sortingLayerID = 0;
                renderer.sortingOrder = 0;
                renderer.spriteSortPoint = SpriteSortPoint.Pivot;
                SetZPosition(renderer.transform, GetZIndex(layer, sortOrder));
            }

            /// <summary>
            /// Moves a transform in the playfield while preserving its world-space depth.
            /// </summary>
            /// <param name="transform">The transform to move.</param>
            /// <param name="position">The desired world-space X and Y coordinates.</param>
            internal static void SetPosition(Transform transform, Vector3 position)
            {
                position.z = transform.position.z;
                transform.position = position;
            }

            /// <summary>
            /// Moves a child in its parent plane while preserving its local depth.
            /// </summary>
            /// <param name="transform">The transform to move.</param>
            /// <param name="position">The desired local X and Y coordinates.</param>
            internal static void SetLocalPosition(Transform transform, Vector3 position)
            {
                position.z = transform.localPosition.z;
                transform.localPosition = position;
            }

            /// <summary>
            /// Changes a transform's depth without changing its playfield coordinates.
            /// </summary>
            /// <param name="transform">The transform to position.</param>
            /// <param name="z">The desired world-space Z coordinate.</param>
            internal static void SetZPosition(Transform transform, float z)
            {
                var position = transform.position;
                position.z = z;
                transform.position = position;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static SensorArea GetSensor(char areaPos, int startPos)
        {
            switch (areaPos)
            {
                case 'A':
                    return (SensorArea)(startPos - 1);
                case 'B':
                    return (SensorArea)(startPos + 7);
                case 'C':
                    return SensorArea.C;
                case 'D':
                    return (SensorArea)(startPos + 16);
                case 'E':
                    return (SensorArea)(startPos + 24);
                default:
                    return SensorArea.A1;
            }
        }
        public static Vector3 GetTouchAreaPosition(SensorArea area)
        {
            var group = area.GetGroup();
            var index = area.GetIndex();
            // AreaDistance: 
            // C:   0
            // E:   3.1
            // B:   2.21
            // A,D: 4.8
            switch (group)
            {
                case SensorGroup.A:
                    {
                        var distance = GetTouchAreaDistance(group);
                        var angle = -index * (Mathf.PI / 4) + Mathf.PI * 5 / 8;
                        return new Vector3(Mathf.Cos(angle), Mathf.Sin(angle)) * distance;
                    }
                case SensorGroup.B:
                    {
                        var distance = GetTouchAreaDistance(group);
                        var angle = -index * (Mathf.PI / 4) + Mathf.PI * 5 / 8;
                        return new Vector3(Mathf.Cos(angle), Mathf.Sin(angle)) * distance;
                    }
                case SensorGroup.C:
                    return Vector3.zero;
                case SensorGroup.D:
                    {
                        var distance = GetTouchAreaDistance(group);
                        var angle = -index * (Mathf.PI / 4) + Mathf.PI * 6 / 8;
                        return new Vector3(Mathf.Cos(angle), Mathf.Sin(angle)) * distance;
                    }
                case SensorGroup.E:
                    {
                        var distance = GetTouchAreaDistance(group);
                        var angle = -index * (Mathf.PI / 4) + Mathf.PI * 6 / 8;
                        return new Vector3(Mathf.Cos(angle), Mathf.Sin(angle)) * distance;
                    }
                default:
                    return Vector3.zero;

            }
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 GetTouchAreaPosition(int index, char area)
        {
            return GetTouchAreaPosition(area switch
            {
                'A' => SensorArea.A1 + (index - 1),
                'B' => SensorArea.B1 + (index - 1),
                'C' => SensorArea.C,
                'D' => SensorArea.D1 + (index - 1),
                'E' => SensorArea.E1 + (index - 1),
                _ => throw new ArgumentOutOfRangeException(nameof(index)),
            });
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float GetTouchAreaDistance(SensorGroup group)
        {
            switch (group)
            {
                case SensorGroup.A:
                    return 4.0f;
                case SensorGroup.B:
                    return 2.2f;
                default:
                    return 0;
                case SensorGroup.D:
                    return 4.1f;
                case SensorGroup.E:
                    return 3.1f;
            }
        }
        public static Quaternion GetTouchRoation(Vector3 position, SensorArea sensorPos)
        {
            if (sensorPos == SensorArea.C)
                return Quaternion.Euler(Vector3.zero);
            var d = Vector3.zero - position;
            var deg = 180 + Mathf.Atan2(d.x, d.y) * Mathf.Rad2Deg;

            return Quaternion.Euler(new Vector3(0, 0, -deg));
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 GetTapPosition(SensorArea area, float distance)
        {
            var group = area.GetGroup();
            switch(group)
            {
                case SensorGroup.A:
                    return GetTapPosition((int)area + 1, distance); 
                case SensorGroup.D:
                    return GetTapPosition((int)area - 16.5f, distance);
                default:
                    throw new ArgumentOutOfRangeException(nameof(area));
            }
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 GetTapPosition(int position, float distance)
        {
            return new Vector3(
                distance * Mathf.Cos((position * -2f + 5f) * 0.125f * Mathf.PI),
                distance * Mathf.Sin((position * -2f + 5f) * 0.125f * Mathf.PI));
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector3 GetTapPosition(float position, float distance)
        {
            return new Vector3(
                distance * Mathf.Cos((position * -2f + 5f) * 0.125f * Mathf.PI),
                distance * Mathf.Sin((position * -2f + 5f) * 0.125f * Mathf.PI));
        }
        public static SlideOKShape GetSlideOKShapeFromSlideType(string slideType)
        {
            switch(slideType)
            {
                case "circle1":
                case "circle2":
                case "circle3":
                case "circle4":
                case "circle5":
                case "circle6":
                case "circle7":
                case "circle8":
                    return SlideOKShape.Curv;
                case "wifi":
                    return SlideOKShape.Wifi;
                default:
                    return SlideOKShape.Str;
            }
        }
    }
}
