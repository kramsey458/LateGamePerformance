using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using UnityEngine;

namespace LateGamePerformance
{
    // Two things the water renderer does every tick on the main thread (WaterRenderer.UpdateMeshAndTextures) that
    // change nothing on screen. Rendering only: the simulation never reads any of it.
    //
    // Tiles. The water surface is drawn in 16 x 16 tiles, one per water layer. Every tick the game switches every
    // tile it ever made off (WaterMesh.DisableAllTiles) and then the ones with water on again (EnableTile), a
    // native Unity call each. The mod remembers which tiles it left on and only switches the ones whose state
    // really changes; the tiles that are on afterwards are the same.
    //
    // Uploads. Each of the water's data textures exists twice, the state before and after the tick, and every
    // tick the game swaps them and then uploads both, layer by layer (DataTextureArray.UpdateTextureArrays). For
    // the flow directions and the flow limits the "before" upload sends exactly what is already on the graphics
    // card: it is what the game uploaded as "after" one tick earlier, into the same texture, from the same array,
    // which nothing has written since. (For depths, contamination, columns and link barriers the game edits the
    // "before" data between the two uploads, so those are left alone.) The mod proves that per layer: same texture,
    // same array, and exactly one data swap in between (counted in DataTextureArray.SwapDataAndClear); otherwise the
    // game uploads both as usual.
    //
    // WaterRendering = false in the settings file turns both off.
    internal static class WaterRendering
    {
        private static bool _tilesActive;
        private static bool _uploadsActive;

        // Tiles
        private static Func<object, Dictionary<Vector3Int, MeshRenderer>> _createdTiles;
        private static object _mesh;
        private static bool _known;
        private static bool _inRendererUpdate;
        private static bool _batch;
        private static HashSet<Vector3Int> _enabled = new HashSet<Vector3Int>();
        private static HashSet<Vector3Int> _desired = new HashSet<Vector3Int>();
        private static long _tileUpdates;
        private static long _switchesMade;
        private static long _switchesGameWouldMake;

        // Uploads
        internal sealed class Tracked
        {
            public object Instance;
            public int Swaps;
            public Texture2DArray[] UploadedTo = new Texture2DArray[0];
            public object[] UploadedFrom = new object[0];
            public int[] SwapsAtUpload = new int[0];

            public void Ensure(int layer)
            {
                if (layer >= UploadedTo.Length)
                {
                    int size = layer + 4;
                    Array.Resize(ref UploadedTo, size);
                    Array.Resize(ref UploadedFrom, size);
                    Array.Resize(ref SwapsAtUpload, size);
                }
            }
        }

        private static Func<object, object> _rendererOutflows;
        private static Func<object, object> _rendererFlowLimits;
        private static Tracked _outflows = new Tracked();
        private static Tracked _flowLimits = new Tracked();
        private static long _layerUploads;
        private static long _halvesSkipped;
        // Harmony runs a postfix after a prefix that skipped the method too; this tells it the prefix did.
        private static bool _uploadHandled;

        public static Feature CreateTilesFeature()
        {
            Type self = typeof(WaterRendering);
            const string renderer = "Timberborn.WaterSystemRendering.WaterRenderer";
            const string mesh = "Timberborn.WaterSystemRendering.WaterMesh";
            Feature feature = new Feature { Name = "WaterTiles" };
            feature.Patches.Add(new PatchSpec
            {
                Name = "WaterRenderer.UpdateMeshAndTextures",
                Required = true,
                Target = () =>
                {
                    _createdTiles = Reflect.FieldGetter<Dictionary<Vector3Int, MeshRenderer>>(Reflect.GameType(mesh), "_createdTiles");
                    return Reflect.Method(renderer, "UpdateMeshAndTextures");
                },
                Prefix = Reflect.Own(self, nameof(RendererUpdatePrefix)),
                Finalizer = Reflect.Own(self, nameof(RendererUpdateFinalizer))
            });
            feature.Patches.Add(new PatchSpec
            {
                Name = "WaterMesh.DisableAllTiles",
                Required = true,
                Target = () => Reflect.Method(mesh, "DisableAllTiles"),
                Prefix = Reflect.Own(self, nameof(DisableAllTilesPrefix))
            });
            feature.Patches.Add(new PatchSpec
            {
                Name = "WaterMesh.EnableTile",
                Required = true,
                Target = () => Reflect.Method(mesh, "EnableTile"),
                Prefix = Reflect.Own(self, nameof(EnableTilePrefix)),
                Postfix = Reflect.Own(self, nameof(EnableTilePostfix))
            });
            return feature;
        }

        public static Feature CreateUploadsFeature()
        {
            Type self = typeof(WaterRendering);
            const string renderer = "Timberborn.WaterSystemRendering.WaterRenderer";
            Feature feature = new Feature { Name = "WaterUploads" };
            feature.Patches.Add(new PatchSpec
            {
                Name = "WaterRenderer.UpdateMeshAndTextures",
                Required = true,
                Target = () =>
                {
                    Type type = Reflect.GameType(renderer);
                    _rendererOutflows = Reflect.FieldGetter<object>(type, "_outflows");
                    _rendererFlowLimits = Reflect.FieldGetter<object>(type, "_flowLimits");
                    Uploads<Vector2>.Bind();
                    Uploads<float>.Bind();
                    return Reflect.Method(renderer, "UpdateMeshAndTextures");
                },
                Prefix = Reflect.Own(self, nameof(CaptureTexturesPrefix))
            });
            feature.Patches.Add(new PatchSpec
            {
                Name = "DataTextureArray<Vector2>.UpdateTextureArrays",
                Required = true,
                Target = () => HarmonyLib.AccessTools.Method(Uploads<Vector2>.Closed(), "UpdateTextureArrays"),
                Prefix = Reflect.Own(self, nameof(UploadVector2Prefix)),
                Postfix = Reflect.Own(self, nameof(UploadPostfix))
            });
            feature.Patches.Add(new PatchSpec
            {
                Name = "DataTextureArray<float>.UpdateTextureArrays",
                Required = true,
                Target = () => HarmonyLib.AccessTools.Method(Uploads<float>.Closed(), "UpdateTextureArrays"),
                Prefix = Reflect.Own(self, nameof(UploadFloatPrefix)),
                Postfix = Reflect.Own(self, nameof(UploadPostfix))
            });
            feature.Patches.Add(new PatchSpec
            {
                Name = "DataTextureArray<Vector2>.SwapDataAndClear",
                Required = true,
                Target = () => HarmonyLib.AccessTools.Method(Uploads<Vector2>.Closed(), "SwapDataAndClear"),
                Postfix = Reflect.Own(self, nameof(SwapPostfix))
            });
            feature.Patches.Add(new PatchSpec
            {
                Name = "DataTextureArray<float>.SwapDataAndClear",
                Required = true,
                Target = () => HarmonyLib.AccessTools.Method(Uploads<float>.Closed(), "SwapDataAndClear"),
                Postfix = Reflect.Own(self, nameof(SwapPostfix))
            });
            return feature;
        }

        public static void ActivateTiles()
        {
            _tilesActive = true;
        }

        public static void ActivateUploads()
        {
            _uploadsActive = true;
        }

        public static void SceneCreated()
        {
            _mesh = null;
            _known = false;
            _batch = false;
            _enabled.Clear();
            _desired.Clear();
            _outflows = new Tracked();
            _flowLimits = new Tracked();
        }

        public static string TakeStatsLine()
        {
            if (!_tilesActive && !_uploadsActive && _tileUpdates == 0 && _layerUploads == 0)
            {
                return null;
            }
            string line = string.Format(CultureInfo.InvariantCulture,
                "WaterRendering: {0} updates switched water tiles {1} times where the game switches them {2} times; " +
                "{3} of {4} flow direction and flow limit uploads left out because the graphics card already had them",
                _tileUpdates, _switchesMade, _switchesGameWouldMake, _halvesSkipped, _layerUploads * 2);
            _tileUpdates = _switchesMade = _switchesGameWouldMake = _layerUploads = _halvesSkipped = 0;
            return line;
        }

        // ---- Tiles ----

        internal static void RendererUpdatePrefix()
        {
            _inRendererUpdate = true;
        }

        internal static void RendererUpdateFinalizer()
        {
            _inRendererUpdate = false;
            if (!_batch)
            {
                return;
            }
            _batch = false;
            try
            {
                Dictionary<Vector3Int, MeshRenderer> tiles = _createdTiles(_mesh);
                foreach (Vector3Int tile in _enabled)
                {
                    if (!_desired.Contains(tile) && tiles.TryGetValue(tile, out MeshRenderer renderer) && renderer != null)
                    {
                        renderer.enabled = false;
                        _switchesMade++;
                    }
                }
                HashSet<Vector3Int> nowEnabled = _desired;
                _desired = _enabled;
                _enabled = nowEnabled;
                _desired.Clear();
                _tileUpdates++;
                _switchesGameWouldMake += tiles.Count + _enabled.Count;
            }
            catch (Exception exception)
            {
                TilesFailed(exception);
            }
        }

        // ReSharper disable once InconsistentNaming
        [HarmonyPriority(Priority.Last)]
        internal static bool DisableAllTilesPrefix(object __instance)
        {
            if (!_tilesActive)
            {
                return true;
            }
            if (!ReferenceEquals(__instance, _mesh))
            {
                _mesh = __instance;
                _known = false;
                _enabled.Clear();
            }
            _desired.Clear();
            _batch = _inRendererUpdate;
            if (_batch && _known)
            {
                // Tiles are switched off after the game has said which ones stay on.
                return false;
            }
            // The game's way this once: every tile off, so the mod knows where it stands.
            _enabled.Clear();
            _known = true;
            return true;
        }

        // ReSharper disable once InconsistentNaming
        [HarmonyPriority(Priority.Last)]
        internal static bool EnableTilePrefix(object __instance, Vector3Int tileIndex)
        {
            if (!_tilesActive || !_known || !ReferenceEquals(__instance, _mesh))
            {
                return true;
            }
            if (_batch)
            {
                _desired.Add(tileIndex);
            }
            // Already on: switching it on again changes nothing.
            return !_enabled.Contains(tileIndex);
        }

        // ReSharper disable once InconsistentNaming
        internal static void EnableTilePostfix(object __instance, Vector3Int tileIndex)
        {
            if (_tilesActive && _known && ReferenceEquals(__instance, _mesh) && _enabled.Add(tileIndex))
            {
                _switchesMade++;
            }
        }

        private static void TilesFailed(Exception exception)
        {
            // Next tick the game switches every tile off and the right ones on again.
            _tilesActive = false;
            _batch = false;
            Log.Warning("WaterRendering (tiles) failed and turned itself off for this session: " + exception);
        }

        // ---- Uploads ----

        // ReSharper disable once InconsistentNaming
        internal static void CaptureTexturesPrefix(object __instance)
        {
            if (!_uploadsActive)
            {
                return;
            }
            try
            {
                object outflows = _rendererOutflows(__instance);
                object flowLimits = _rendererFlowLimits(__instance);
                if (!ReferenceEquals(_outflows.Instance, outflows))
                {
                    _outflows = new Tracked { Instance = outflows };
                }
                if (!ReferenceEquals(_flowLimits.Instance, flowLimits))
                {
                    _flowLimits = new Tracked { Instance = flowLimits };
                }
            }
            catch (Exception exception)
            {
                UploadsFailed(exception);
            }
        }

        // Worker thread (the game's SwapWaterTexturesTask); the main thread reads the count after the game has
        // waited for the task.
        // ReSharper disable once InconsistentNaming
        internal static void SwapPostfix(object __instance)
        {
            Tracked tracked = TrackedFor(__instance);
            if (tracked != null)
            {
                Interlocked.Increment(ref tracked.Swaps);
            }
        }

        // ReSharper disable once InconsistentNaming
        [HarmonyPriority(Priority.Last)]
        internal static bool UploadVector2Prefix(object __instance, int columnIndex)
        {
            return Uploads<Vector2>.Prefix(__instance, columnIndex);
        }

        // ReSharper disable once InconsistentNaming
        [HarmonyPriority(Priority.Last)]
        internal static bool UploadFloatPrefix(object __instance, int columnIndex)
        {
            return Uploads<float>.Prefix(__instance, columnIndex);
        }

        // Records what the game uploaded when it uploaded both halves itself.
        // ReSharper disable once InconsistentNaming
        internal static void UploadPostfix(object __instance, int columnIndex)
        {
            if (_uploadHandled)
            {
                _uploadHandled = false;
                return;
            }
            Tracked tracked = TrackedFor(__instance);
            if (tracked == null)
            {
                return;
            }
            try
            {
                if (ReferenceEquals(__instance, _outflows.Instance))
                {
                    Uploads<Vector2>.Record(tracked, __instance, columnIndex);
                }
                else
                {
                    Uploads<float>.Record(tracked, __instance, columnIndex);
                }
                _layerUploads++;
            }
            catch (Exception exception)
            {
                UploadsFailed(exception);
            }
        }

        private static Tracked TrackedFor(object instance)
        {
            if (!_uploadsActive || instance == null)
            {
                return null;
            }
            Tracked outflows = _outflows, flowLimits = _flowLimits;
            return ReferenceEquals(instance, outflows.Instance) ? outflows
                : ReferenceEquals(instance, flowLimits.Instance) ? flowLimits : null;
        }

        private static void UploadsFailed(Exception exception)
        {
            _uploadsActive = false;
            Log.Warning("WaterRendering (uploads) failed and turned itself off for this session: " + exception);
        }

        // Accessors for one closed DataTextureArray<T>, T being a public Unity or system type.
        internal static class Uploads<T> where T : struct
        {
            private static Type _closed;
            private static Func<object, T[][]> _oldData;
            private static Func<object, T[][]> _newData;
            private static Func<object, Texture2D> _buffer;
            private static Func<object, Texture2DArray> _oldArray;
            private static Func<object, Texture2DArray> _newArray;

            public static Type Closed()
            {
                Bind();
                return _closed;
            }

            public static void Bind()
            {
                if (_closed != null)
                {
                    return;
                }
                Type open = Reflect.GameType("Timberborn.WaterSystemRendering.DataTextureArray`1");
                if (open == null)
                {
                    throw new TypeLoadException("DataTextureArray not found");
                }
                Type closed = open.MakeGenericType(typeof(T));
                _oldData = Reflect.FieldGetter<T[][]>(closed, "_oldData");
                _newData = Reflect.FieldGetter<T[][]>(closed, "_newData");
                _buffer = Reflect.FieldGetter<Texture2D>(closed, "_bufferTexture");
                _oldArray = Reflect.PropertyGetter<Texture2DArray>(closed, "OldArray");
                _newArray = Reflect.PropertyGetter<Texture2DArray>(closed, "NewArray");
                _closed = closed;
            }

            [HarmonyPriority(Priority.Last)]
            public static bool Prefix(object instance, int layer)
            {
                Tracked tracked = TrackedFor(instance);
                if (tracked == null)
                {
                    return true;
                }
                try
                {
                    tracked.Ensure(layer);
                    Texture2DArray oldArray = _oldArray(instance);
                    T[] oldData = _oldData(instance)[layer];
                    bool alreadyThere = oldArray != null &&
                                        ReferenceEquals(tracked.UploadedTo[layer], oldArray) &&
                                        ReferenceEquals(tracked.UploadedFrom[layer], oldData) &&
                                        Volatile.Read(ref tracked.Swaps) == tracked.SwapsAtUpload[layer] + 1;
                    if (!alreadyThere)
                    {
                        return true;
                    }
                    // The game's second half, as the game does it.
                    Texture2D buffer = _buffer(instance);
                    Texture2DArray newArray = _newArray(instance);
                    T[] newData = _newData(instance)[layer];
                    buffer.SetPixelData(newData, 0);
                    buffer.Apply(false, false);
                    Graphics.CopyTexture(buffer, 0, 0, newArray, layer, 0);
                    Remember(tracked, layer, newArray, newData);
                    _layerUploads++;
                    _halvesSkipped++;
                    _uploadHandled = true;
                    return false;
                }
                catch (Exception exception)
                {
                    UploadsFailed(exception);
                    return true;
                }
            }

            public static void Record(Tracked tracked, object instance, int layer)
            {
                tracked.Ensure(layer);
                Remember(tracked, layer, _newArray(instance), _newData(instance)[layer]);
            }

            private static void Remember(Tracked tracked, int layer, Texture2DArray uploadedTo, T[] uploadedFrom)
            {
                tracked.UploadedTo[layer] = uploadedTo;
                tracked.UploadedFrom[layer] = uploadedFrom;
                tracked.SwapsAtUpload[layer] = Volatile.Read(ref tracked.Swaps);
            }
        }
    }
}
