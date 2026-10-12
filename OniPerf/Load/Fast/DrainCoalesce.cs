using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;

namespace OniPerf
{
	/// <summary>
	/// Repeated refreshes inside one drain frame's builds, done once at the end of the frame.
	///
	/// **The mechanism.** Two of the drain's per-object costs (measured over the Euphoria
	/// drain) are refreshes that neighbouring objects repeat:
	///
	///   - `TileVisualizer.RefreshCell` (1.67 s): each tile spawned refreshes its own cell and its four
	///     neighbours on two layers (`RefreshCellInternal`: a BlockTileRenderer rebuild and the
	///     KAnimGraphTileVisualizer refresh), so a block of tiles built together refreshes most cells
	///     up to five times, each time from a half-built neighbourhood.
	///   - `DecorProvider.Refresh` (2.55 s): Clear() then AddDecor(), a visibility-tested splat over the
	///     decor radius. It runs at OnSpawn and again on every OnDirty of the decor attributes (each
	///     modifier BuildingComplete.OnSpawn adds for the construction materials) and on cell change.
	///
	/// **The lever.** While the drain is building (DeferredObject sets <see cref="Active"/> around
	/// its build loop only, never across the game's own frames), the calls are queued, deduplicated,
	/// and run once each in first-call order before the drain frame yields. Each refresh reads the
	/// current state, so the last one is the one that counts and the state at the frame boundary is
	/// what Klei's last call would have left. A DecorProvider destroyed before the flush is skipped.
	///
	/// The building digest cannot see decor, so SmTrip logs a Grid.Decor hash beside it.
	/// Off unless the drain coalescing.
	/// </summary>
	internal static class DrainCoalesce
	{
		public static bool Active;

		private static bool installed;
		private static readonly List<DecorProvider> decor = new List<DecorProvider>();
		private static readonly HashSet<DecorProvider> decorSeen = new HashSet<DecorProvider>();
		private static readonly List<long> tiles = new List<long>();
		private static readonly HashSet<long> tilesSeen = new HashSet<long>();
		private static System.Action<int, ObjectLayer> refreshInternal;
		private static long decorCalls, decorRuns, tileCalls, tileRuns;

		public static void Install(Harmony harmony)
		{
			if (installed)
			{
				return;
			}
			System.Reflection.MethodInfo cell = AccessTools.DeclaredMethod(typeof(TileVisualizer), "RefreshCellInternal");
			System.Reflection.MethodInfo refresh = AccessTools.DeclaredMethod(typeof(DecorProvider), nameof(DecorProvider.Refresh));
			if (cell == null || refresh == null)
			{
				PerfLog.Warn("drain-coalesce: no TileVisualizer.RefreshCellInternal / DecorProvider.Refresh; off");
				return;
			}
			try
			{
				refreshInternal = AccessTools.MethodDelegate<System.Action<int, ObjectLayer>>(cell);
				harmony.Patch(cell, prefix: new HarmonyMethod(typeof(DrainCoalesce), nameof(CellPrefix)));
				harmony.Patch(refresh, prefix: new HarmonyMethod(typeof(DrainCoalesce), nameof(DecorPrefix)));
				installed = true;
			}
			catch (System.Exception e)
			{
				PerfLog.Warn("drain-coalesce: could not patch: " + e.Message);
				return;
			}
			PerfLog.Info("drain-coalesce: installed on TileVisualizer.RefreshCellInternal and DecorProvider.Refresh");
		}

		/// <summary>Start of a drain frame's builds.</summary>
		public static void Begin()
		{
			Active = installed;
		}

		private static bool CellPrefix(int cell, ObjectLayer tile_layer)
		{
			if (!Active)
			{
				return true;
			}
			tileCalls++;
			long key = ((long)tile_layer << 32) | (uint)cell;
			if (tilesSeen.Add(key))
			{
				tiles.Add(key);
			}
			return false;
		}

		private static bool DecorPrefix(DecorProvider __instance)
		{
			if (!Active)
			{
				return true;
			}
			decorCalls++;
			if (decorSeen.Add(__instance))
			{
				decor.Add(__instance);
			}
			return false;
		}

		/// <summary>End of a drain frame's builds, and every way out of the drain: run what was queued.</summary>
		public static void Flush()
		{
			Active = false;
			// Decor first: Klei's tile refreshes do not read decor, and a decor splat does not read
			// the tile visualizers, so the order between the two kinds does not matter.
			for (int i = 0; i < decor.Count; i++)
			{
				DecorProvider p = decor[i];
				if (p != null)
				{
					decorRuns++;
					p.Refresh();
				}
			}
			decor.Clear();
			decorSeen.Clear();
			for (int i = 0; i < tiles.Count; i++)
			{
				long key = tiles[i];
				tileRuns++;
				refreshInternal((int)(uint)key, (ObjectLayer)(int)(key >> 32));
			}
			tiles.Clear();
			tilesSeen.Clear();
		}

		public static void Report()
		{
			if (!installed)
			{
				return;
			}
			PerfLog.Info(string.Format(CultureInfo.InvariantCulture,
				"drain-coalesce: DecorProvider.Refresh {0:N0} calls run as {1:N0}; TileVisualizer.RefreshCellInternal {2:N0} calls run as {3:N0}",
				decorCalls, decorRuns, tileCalls, tileRuns));
			decorCalls = decorRuns = tileCalls = tileRuns = 0;
		}
	}
}
