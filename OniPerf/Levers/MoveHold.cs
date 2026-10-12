using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace OniPerf
{
	/// <summary>
	/// A moving transform stays "moving" for <see cref="HoldS"/> of real time after its last
	/// position change, so the frames between sim sub-ticks stop flipping it to "stopped" and back.
	///
	/// **The mechanism, measured.** At steady speed 1,
	/// `CellChangeMonitor.RenderEveryTick` is 1.47 ms a frame: 1,609 dirty transforms and 811
	/// handler calls a frame, 0.84 ms of them in handlers. The top three are movement-state
	/// handlers: `KBatchedAnimController` 408 calls a frame (0.49 ms: a `GetComponent`, a
	/// `SetDirty`, and its update listener re-registered), `KCollider2D` 176 (0.12, its partitioner
	/// entry re-marked) and `KBatchedAnimUpdater` 202 (0.08). The monitor calls a transform "moving"
	/// on a frame when it was marked dirty since the last frame and "stopped" on the first frame it
	/// was not. A walking dupe's transform is moved by `UpdateNavigator`, a `SIM_EVERY_TICK`
	/// bucket that runs in only 58% of frames at 100 fps, so every walking dupe (and each of its
	/// child transforms) reads stopped on the frames without a sub-tick and moving again on the
	/// next: the handlers fire on nearly every frame for the whole walking colony. The faster the
	/// frame, the more frames fall between sub-ticks.
	///
	/// **The lever.** A prefix on `RenderEveryTick` records the real time each transform in the
	/// pending dirty set was last marked, then re-adds to that set every transform that was moving
	/// last frame, is not dirty now, and was marked within <see cref="HoldS"/>. Klei's method then
	/// sees it dirty: no stop event, its cell re-checked (unchanged, no cell event), still moving.
	/// Start events are unchanged, and a transform that really stops gets its stop event
	/// <see cref="HoldS"/> late, during which it is updated as a moving object. **Every speed.**
	///
	/// Switched by the `moveHold` key in config.json (off by default).
	/// </summary>
	internal static class MoveHold
	{
		public static bool Active;

		public const float HoldS = 0.1f;

		private static AccessTools.FieldRef<CellChangeMonitor, HybridListHashSet<int>> pendingRef, movingRef;
		private static readonly Dictionary<int, float> lastMarked = new Dictionary<int, float>();
		private static readonly List<int> stale = new List<int>();
		private static long frames, held;
		private static float lastSweep;
		private static bool installed;

		public static void Install(Harmony harmony)
		{
			if (installed)
			{
				return;
			}
			var tick = AccessTools.DeclaredMethod(typeof(CellChangeMonitor), "RenderEveryTick");
			try
			{
				pendingRef = AccessTools.FieldRefAccess<CellChangeMonitor, HybridListHashSet<int>>("pendingDirtyTransforms");
				movingRef = AccessTools.FieldRefAccess<CellChangeMonitor, HybridListHashSet<int>>("movingTransforms");
			}
			catch (Exception e)
			{
				PerfLog.Warn("movehold: fields not found (" + e.Message + "); off");
				return;
			}
			if (tick == null)
			{
				PerfLog.Warn("movehold: CellChangeMonitor.RenderEveryTick not found; off");
				return;
			}
			try
			{
				harmony.Patch(tick, prefix: new HarmonyMethod(typeof(MoveHold), nameof(Prefix)));
				installed = true;
			}
			catch (Exception e)
			{
				PerfLog.Warn("movehold: could not patch: " + e.Message);
				return;
			}
			PerfLog.Info("movehold: installed on CellChangeMonitor.RenderEveryTick, hold " + HoldS + " s");
		}

		private static void Prefix(CellChangeMonitor __instance)
		{
			if (!Active)
			{
				return;
			}
			HybridListHashSet<int> pending = pendingRef(__instance);
			HybridListHashSet<int> moving = movingRef(__instance);
			if (pending == null || moving == null)
			{
				return;
			}
			float now = Time.realtimeSinceStartup;
			int count = pending.Count;
			for (int i = 0; i < count; i++)
			{
				lastMarked[pending[i]] = now;
			}
			// `moving` still holds last frame's set here; Klei swaps it out after this prefix.
			int movingCount = moving.Count;
			for (int i = 0; i < movingCount; i++)
			{
				int id = moving[i];
				float at;
				if (!pending.Contains(id) && lastMarked.TryGetValue(id, out at) && now - at < HoldS)
				{
					pending.Add(id);
					held++;
				}
			}
			frames++;
			// Forget transforms not marked for a while, so destroyed objects do not accumulate.
			if (now - lastSweep > 5f)
			{
				lastSweep = now;
				stale.Clear();
				foreach (KeyValuePair<int, float> kv in lastMarked)
				{
					if (now - kv.Value > 1f)
					{
						stale.Add(kv.Key);
					}
				}
				for (int i = 0; i < stale.Count; i++)
				{
					lastMarked.Remove(stale[i]);
				}
			}
		}

		public static void Reset()
		{
			frames = held = 0;
		}

		public static void LogSummary(string what)
		{
			PerfLog.Info("movehold: " + what + " -- " + (frames > 0 ? ((double)held / frames).ToString("0.0") : "0")
				+ " transforms held moving a frame over " + frames + " frames; " + lastMarked.Count + " tracked");
		}
	}
}
