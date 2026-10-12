using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace OniPerf
{
	/// <summary>
	/// The gas and liquid pipe contents, rebuilt every rendered frame, reused while nothing that
	/// feeds them has changed.
	///
	/// **The mechanism, measured.** `Game.LateUpdate` calls
	/// `ConduitFlowVisualizer.Render` for the gas and liquid visualizers every frame: 0.21 ms of a
	/// 4.75 ms paused frame. `RenderMesh` walks every conduit in the world to find the visible ones,
	/// runs a job over them to lay out every blob, then clears and refills two Unity meshes and
	/// issues one `Graphics.DrawMesh` each. Paused, every input is frozen: the flow data only moves on
	/// sim ticks, the lerp fraction and the animation clock stand still, so the meshes come out
	/// identical frame after frame.
	///
	/// **The lever.** Before `RenderMesh` runs, a key is taken from everything it reads that can
	/// change between frames: the visible cell area, the lerp fraction, the animation clock, the
	/// contents/layer/highlight state, the conduit count and the overlay mode. When the key equals
	/// the last build's and the last build is younger than <see cref="MaxAgeS"/>, the two meshes from
	/// that build are drawn again exactly as `ConduitFlowMesh.End` draws them, and the rebuild is
	/// skipped. The age cap bounds anything the key misses to half a second of stale blobs. A frame
	/// that triggers pipe audio always rebuilds. At speed the lerp fraction moves every frame, so the
	/// key never matches: **a paused-only saving**.
	///
	/// Switched by the `pipeCache` key in config.json (on by default).
	/// </summary>
	internal static class PipeCache
	{
		public static bool Active;

		public const float MaxAgeS = 0.5f;

		private struct Key : IEquatable<Key>
		{
			public int MinX, MinY, MaxX, MaxY, Layer, Highlighted, Conduits;
			public float Lerp, Z;
			public double AnimTime;
			public bool Contents;
			public HashedString Overlay;

			public bool Equals(Key o)
			{
				return MinX == o.MinX && MinY == o.MinY && MaxX == o.MaxX && MaxY == o.MaxY && Layer == o.Layer
					&& Highlighted == o.Highlighted && Conduits == o.Conduits && Lerp == o.Lerp && Z == o.Z
					&& AnimTime == o.AnimTime && Contents == o.Contents && Overlay == o.Overlay;
			}
		}

		private sealed class Cache
		{
			public Key Key;
			public bool Valid;
			public bool Drew;
			public float BuiltAt;
		}

		private static readonly Dictionary<ConduitFlowVisualizer, Cache> caches = new Dictionary<ConduitFlowVisualizer, Cache>();
		private static Cache building;

		private static FieldInfo flowManagerField, showContentsField, animTimeField, layerField, highlightedField,
			movingField, staticField, meshField, materialField, gridOffsetField;
		private static long rebuilt, reused;
		private static bool installed;

		public static void Install(Harmony harmony)
		{
			if (installed)
			{
				return;
			}
			Type vis = typeof(ConduitFlowVisualizer);
			Type flowMesh = AccessTools.Inner(vis, "ConduitFlowMesh");
			var renderMesh = AccessTools.DeclaredMethod(vis, "RenderMesh");
			var end = flowMesh == null ? null : AccessTools.DeclaredMethod(flowMesh, "End");
			flowManagerField = AccessTools.Field(vis, "flowManager");
			showContentsField = AccessTools.Field(vis, "showContents");
			animTimeField = AccessTools.Field(vis, "animTime");
			layerField = AccessTools.Field(vis, "layer");
			highlightedField = AccessTools.Field(vis, "highlightedCell");
			movingField = AccessTools.Field(vis, "movingBallMesh");
			staticField = AccessTools.Field(vis, "staticBallMesh");
			gridOffsetField = AccessTools.Field(vis, "GRID_OFFSET");
			meshField = flowMesh == null ? null : AccessTools.Field(flowMesh, "mesh");
			materialField = flowMesh == null ? null : AccessTools.Field(flowMesh, "material");
			if (renderMesh == null || end == null || flowManagerField == null || showContentsField == null
				|| animTimeField == null || layerField == null || highlightedField == null || movingField == null
				|| staticField == null || gridOffsetField == null || meshField == null || materialField == null)
			{
				PerfLog.Warn("pipecache: ConduitFlowVisualizer is not the shape this lever was written against; off");
				return;
			}
			try
			{
				harmony.Patch(renderMesh, prefix: new HarmonyMethod(typeof(PipeCache), nameof(RenderMeshPrefix)),
					finalizer: new HarmonyMethod(typeof(PipeCache), nameof(RenderMeshFinalizer)));
				harmony.Patch(end, postfix: new HarmonyMethod(typeof(PipeCache), nameof(EndPostfix)));
				installed = true;
			}
			catch (Exception e)
			{
				PerfLog.Warn("pipecache: could not patch: " + e.Message);
				return;
			}
			PerfLog.Info("pipecache: installed on ConduitFlowVisualizer.RenderMesh");
		}

		private static bool RenderMeshPrefix(ConduitFlowVisualizer __instance, float z, float lerp_percent, bool trigger_audio)
		{
			building = null;
			if (!caches.TryGetValue(__instance, out Cache cache))
			{
				cache = new Cache();
				caches[__instance] = cache;
			}
			if (!Active || trigger_audio)
			{
				cache.Valid = false;
				return true;
			}
			GridArea area = GridVisibleArea.GetVisibleArea();
			var flow = (ConduitFlow)flowManagerField.GetValue(__instance);
			var key = new Key
			{
				MinX = area.Min.x,
				MinY = area.Min.y,
				MaxX = area.Max.x,
				MaxY = area.Max.y,
				Layer = (int)layerField.GetValue(__instance),
				Highlighted = (int)highlightedField.GetValue(__instance),
				Conduits = flow.soaInfo.NumEntries,
				Lerp = lerp_percent,
				Z = z,
				AnimTime = (double)animTimeField.GetValue(__instance),
				Contents = (bool)showContentsField.GetValue(__instance),
				Overlay = SimDebugView.Instance != null ? SimDebugView.Instance.GetMode() : default(HashedString),
			};
			float now = Time.realtimeSinceStartup;
			if (cache.Valid && cache.Key.Equals(key) && now - cache.BuiltAt < MaxAgeS)
			{
				if (cache.Drew)
				{
					Redraw(__instance, movingField, z, key.Layer);
					Redraw(__instance, staticField, z, key.Layer);
				}
				reused++;
				return false;
			}
			cache.Key = key;
			cache.Valid = true;
			cache.Drew = false;
			cache.BuiltAt = now;
			building = cache;
			rebuilt++;
			return true;
		}

		private static Exception RenderMeshFinalizer(ConduitFlowVisualizer __instance, Exception __exception)
		{
			if (__exception != null && caches.TryGetValue(__instance, out Cache cache))
			{
				cache.Valid = false;
			}
			building = null;
			return __exception;
		}

		private static void EndPostfix()
		{
			if (building != null)
			{
				building.Drew = true;
			}
		}

		// ConduitFlowMesh.End: Graphics.DrawMesh(mesh, new Vector3(GRID_OFFSET.x, GRID_OFFSET.y, z - 0.1f),
		// Quaternion.identity, material, layer), after refilling the mesh this skips.
		private static void Redraw(ConduitFlowVisualizer vis, FieldInfo which, float z, int layer)
		{
			object flowMesh = which.GetValue(vis);
			var mesh = (Mesh)meshField.GetValue(flowMesh);
			var material = (Material)materialField.GetValue(flowMesh);
			var offset = (Vector2)gridOffsetField.GetValue(null);
			Graphics.DrawMesh(mesh, new Vector3(offset.x, offset.y, z - 0.1f), Quaternion.identity, material, layer);
		}

		public static void Reset()
		{
			rebuilt = reused = 0;
			foreach (Cache cache in caches.Values)
			{
				cache.Valid = false;
			}
		}

		public static void LogSummary(string what)
		{
			PerfLog.Info("pipecache: " + what + " -- " + rebuilt + " rebuilt, " + reused + " reused");
		}
	}
}
