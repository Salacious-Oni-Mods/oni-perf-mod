using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace OniPerf
{
	/// <summary>
	/// Property textures refilled when their contents change, instead of every rendered frame.
	///
	/// **The mechanism.** `PropertyTextures.LateUpdate` costs 1.26 ms of an 8 ms paused frame
	/// and it does two separate things:
	///
	///   1. It refills texture data. `updateEveryFrame` is true for **9 of the 17** properties, and
	///      one more is refilled round-robin, so about ten properties are rebuilt per frame. An
	///      internally-filled property locks its visible region and fills it across worker threads
	///      through `GlobalJobManager.Run`, which the main thread waits on; an externally-updated one
	///      calls `LoadRawTextureData(ptr, 4-8 bytes x Grid.WidthInCells x Grid.HeightInCells)` and
	///      `Apply()` -- **the whole map, not the visible region.**
	///   2. It advances the `TextureLerper` blend, once per frame, which is an interpolation and
	///      legitimately per-frame.
	///
	/// **What decides when the contents change: the sim tick, not the frame.** `PropertyTextures` is
	/// an `ISim200ms`, and its `Sim200ms(dt)` is what calls `TextureLerper.LongUpdate(dt)` to set the
	/// blend period. So the data behind these textures changes **5 times a second** at speed 1, and
	/// about 15 at speed 3, while the refill runs 96 times a second. That is the same shape as the
	/// brain lever: a limit expressed in frames where the thing it tracks moves in sim ticks.
	///
	/// **The gate, and it is exact rather than a heuristic.** A refill is skipped only when both are
	/// true:
	///
	///   - no `Sim200ms` has happened since this property was last filled, so the source data is
	///     byte-for-byte what it was; and
	///   - the visible cell range passed to `UpdateProperty` is identical, so no cell has come into
	///     view. The range is already an argument, so this costs four integer compares.
	///
	/// The lerper blend is never gated, so the visuals still interpolate every frame.
	///
	/// **This one is partly a paused-only saving.** While
	/// paused there are no sim ticks at all, so with a still camera every refill is skipped and this
	/// scores its best possible result. It is not *only* that -- at speed 1 the sim ticks 5 times
	/// a second against 96 frames, so the great majority of refills are still redundant with the
	/// colony running -- but the paused number is an upper bound and the speed-1 window is what
	/// prices it.
	///
	/// Switched by the `propTex` key in config.json (on by default).
	/// </summary>
	internal static class PropTex
	{
		public static bool Active;

		/// <summary>
		/// Incremented by `Sim200ms`. A property filled at generation N holds data that is still
		/// current for as long as the generation is still N.
		/// </summary>
		private static long generation;

		private static readonly Dictionary<int, long> filledAt = new Dictionary<int, long>();
		private static readonly Dictionary<int, int[]> filledRange = new Dictionary<int, int[]>();
		private static FieldInfo simPropertyField;
		private static long refills, skips;
		private static bool installed;

		public static void Install(Harmony harmony)
		{
			if (installed)
			{
				return;
			}
			MethodInfo update = AccessTools.DeclaredMethod(typeof(PropertyTextures), "UpdateProperty");
			MethodInfo sim = AccessTools.DeclaredMethod(typeof(PropertyTextures), "Sim200ms", new[] { typeof(float) });
			if (update == null || sim == null)
			{
				PerfLog.Warn("proptex: no PropertyTextures.UpdateProperty or Sim200ms; off");
				return;
			}
			try
			{
				harmony.Patch(update, prefix: new HarmonyMethod(typeof(PropTex), nameof(UpdatePropertyPrefix)));
				harmony.Patch(sim, postfix: new HarmonyMethod(typeof(PropTex), nameof(SimPostfix)));
				installed = true;
			}
			catch (Exception e)
			{
				PerfLog.Warn("proptex: could not patch: " + e.Message);
				return;
			}
			PerfLog.Info("proptex: installed on PropertyTextures.UpdateProperty (" + update.GetParameters().Length
				+ " args) and Sim200ms");
		}

		private static void SimPostfix()
		{
			generation++;
		}

		/// <summary>
		/// `TextureProperties` is a private nested struct, so the first argument cannot be named in
		/// this assembly. Harmony's `__args` hands it over boxed, which is enough to read the one
		/// field that identifies the property; the other four arguments are the visible cell range.
		/// Returning false skips the original, which is the whole lever.
		/// </summary>
		private static bool UpdatePropertyPrefix(object[] __args)
		{
			if (!Active || __args == null || __args.Length < 5)
			{
				return true;
			}
			object props = __args[0];
			if (props == null)
			{
				return true;
			}
			if (simPropertyField == null)
			{
				simPropertyField = AccessTools.Field(props.GetType(), "simProperty");
				if (simPropertyField == null)
				{
					PerfLog.Warn("proptex: no simProperty field on " + props.GetType().Name + "; lever off");
					Active = false;
					return true;
				}
			}
			int key;
			try
			{
				key = (int)simPropertyField.GetValue(props);
			}
			catch (Exception)
			{
				return true;
			}
			int x0 = (int)__args[1], y0 = (int)__args[2], x1 = (int)__args[3], y1 = (int)__args[4];
			long had;
			int[] range;
			if (filledAt.TryGetValue(key, out had) && had == generation
				&& filledRange.TryGetValue(key, out range)
				&& range[0] == x0 && range[1] == y0 && range[2] == x1 && range[3] == y1)
			{
				// Same sim generation and the same cells on screen: the texture already holds exactly
				// what this call would write.
				skips++;
				return false;
			}
			filledAt[key] = generation;
			filledRange[key] = new[] { x0, y0, x1, y1 };
			refills++;
			return true;
		}

		public static void Reset()
		{
			refills = skips = 0;
			filledAt.Clear();
			filledRange.Clear();
		}

		public static void LogSummary(string what)
		{
			long total = refills + skips;
			PerfLog.Info("proptex: " + what + " -- " + refills + " property refills, " + skips + " skipped ("
				+ (total > 0 ? (100.0 * refills / total).ToString("0.0") : "0") + "% of what vanilla would do)");
		}
	}
}
