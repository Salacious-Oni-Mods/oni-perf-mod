using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using UnityEngine;
using SConduit = SolidConduitFlow.Conduit;

namespace OniPerf
{
	/// <summary>
	/// `SolidConduitFlow.TryAdd`, the same result from an index instead of linear scans.
	///
	/// **The mechanism.** Every sink a conveyor source reaches in the rebuild's DFS copies the DFS
	/// path and hands it to TryAdd, which (1) drops it if some kept path already holds it as a run,
	/// (2) removes every kept path that it holds as a run, (3) strips from it each conduit a kept
	/// path already holds whose permitted flow is a single direction, and appends it. Each step is
	/// a scan of every kept path, (1) and (3) with a `FindIndex` lambda per conduit, so a rebuild
	/// costs (sinks reached) x (paths kept) x (path length).
	///
	/// **The lever.** The kept paths are indexed by conduit: which paths hold it, at what position,
	/// and which paths start with it. A path is never changed once kept (only removed, or the whole
	/// list cleared by RebuildConnections), and a DFS path never holds a conduit twice, so a
	/// position lookup is exactly what `FindIndex` returns. Each step then visits only the paths
	/// that can match:
	///
	///   (1) paths holding the new path's first conduit; the match test is Klei's, quirks kept
	///       (the run test stops before the last position, and a reversed pair passes);
	///   (2) paths starting with one of the new path's conduits, same test the other way round;
	///   (3) a conduit is stripped when any kept path still holds it, which is what the scan asks.
	///
	/// The kept list's order is Klei's (Sim200ms walks it in order): removals keep the order and the
	/// new path goes on the end. With `conveyorPathsCheck` in config.json, the game's own TryAdd
	/// also runs on a copy beside every call and the first differences are logged (slow; for
	/// checking only).
	///
	/// Switched by the `conveyorPaths` key in config.json (on by default). A rebuild is timed, and
	/// the first after a load and any that take 250 ms or more are logged.
	/// </summary>
	internal static class ConveyorPaths
	{
		public static bool Check;

		private static bool installed;
		private static AccessTools.FieldRef<SolidConduitFlow, List<List<SConduit>>> pathListRef;
		private static System.Reflection.MethodInfo tryAdd;

		// The index, for the one SolidConduitFlow there is. `indexed` is the kept list it describes.
		private static List<List<SConduit>> indexed;
		private static int indexedCount;
		private static readonly Dictionary<int, HashSet<List<SConduit>>> holding = new Dictionary<int, HashSet<List<SConduit>>>();
		private static readonly Dictionary<int, List<List<SConduit>>> startingWith = new Dictionary<int, List<List<SConduit>>>();
		private static readonly Dictionary<List<SConduit>, Dictionary<int, int>> positions = new Dictionary<List<SConduit>, Dictionary<int, int>>();

		private static readonly Dictionary<int, int> newPos = new Dictionary<int, int>();
		private static readonly HashSet<List<SConduit>> doomed = new HashSet<List<SConduit>>();
		private static bool inCheck;
		private static long calls, mismatches, rebuilds;
		private static long rebuildStart, rebuildCallsBefore;
		private static int rebuildsLogged;
		private const double LogRebuildMs = 250.0;

		public static void Install(Harmony harmony)
		{
			if (installed)
			{
				return;
			}
			tryAdd = AccessTools.DeclaredMethod(typeof(SolidConduitFlow), "TryAdd");
			System.Reflection.MethodInfo rebuild = AccessTools.DeclaredMethod(typeof(SolidConduitFlow), "RebuildConnections");
			if (tryAdd == null || rebuild == null)
			{
				PerfLog.Warn("conveyorpaths: no SolidConduitFlow.TryAdd / RebuildConnections; off");
				return;
			}
			try
			{
				pathListRef = AccessTools.FieldRefAccess<SolidConduitFlow, List<List<SConduit>>>("pathList");
				harmony.Patch(tryAdd, prefix: new HarmonyMethod(typeof(ConveyorPaths), nameof(TryAddPrefix)));
				harmony.Patch(rebuild, prefix: new HarmonyMethod(typeof(ConveyorPaths), nameof(RebuildPrefix)),
					postfix: new HarmonyMethod(typeof(ConveyorPaths), nameof(RebuildPostfix)));
				installed = true;
			}
			catch (System.Exception e)
			{
				PerfLog.Warn("conveyorpaths: could not patch: " + e.Message);
				return;
			}
			PerfLog.Info("conveyorpaths: installed on SolidConduitFlow.TryAdd" + (Check ? " (check mode: the game's TryAdd runs beside every call)" : ""));
		}

		/// <summary>A new colony: the next rebuild is logged whatever it costs.</summary>
		public static void NewColony()
		{
			rebuildsLogged = 0;
		}

		private static void RebuildPrefix()
		{
			Forget();
			rebuildCallsBefore = calls;
			rebuildStart = System.Diagnostics.Stopwatch.GetTimestamp();
		}

		private static void RebuildPostfix()
		{
			double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - rebuildStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
			if (rebuildsLogged == 0 || ms >= LogRebuildMs)
			{
				rebuildsLogged++;
				PerfLog.Info(string.Format(CultureInfo.InvariantCulture, "conveyorpaths: conveyor rebuild {0:0.0} ms, {1:N0} paths offered{2}",
					ms, calls - rebuildCallsBefore, Check ? ", " + mismatches.ToString("N0", CultureInfo.InvariantCulture) + " mismatches so far" : ""));
			}
		}

		private static void Forget()
		{
			indexed = null;
			holding.Clear();
			startingWith.Clear();
			positions.Clear();
		}

		private static bool TryAddPrefix(SolidConduitFlow __instance, List<SConduit> new_path)
		{
			if (inCheck)
			{
				return true;
			}
			List<List<SConduit>> pathList = pathListRef(__instance);
			calls++;
			if (Check)
			{
				List<List<SConduit>> expect = RunKlei(__instance, pathList, new List<SConduit>(new_path));
				Add(__instance, pathList, new_path);
				Compare(expect, pathList);
			}
			else
			{
				Add(__instance, pathList, new_path);
			}
			return false;
		}

		private static void Add(SolidConduitFlow flow, List<List<SConduit>> pathList, List<SConduit> new_path)
		{
			// Something other than TryAdd changed the list (a clear we did not see, another mod):
			// index it afresh rather than trust it.
			if (!ReferenceEquals(indexed, pathList) || indexedCount != pathList.Count)
			{
				Reindex(pathList);
			}
			int n = new_path.Count;
			int first = new_path[0].idx, last = new_path[n - 1].idx;

			// (1) Already held as a run by a kept path at least as long?
			if (holding.TryGetValue(first, out HashSet<List<SConduit>> withFirst))
			{
				foreach (List<SConduit> path in withFirst)
				{
					if (path.Count < n)
					{
						continue;
					}
					Dictionary<int, int> pos = positions[path];
					if (!pos.TryGetValue(last, out int end))
					{
						continue;
					}
					if (IsRun(path, pos[first], end, new_path))
					{
						return;
					}
				}
			}

			// Empty kept paths go first, as in Klei's; they are never indexed.
			// (2) Kept paths no longer than the new one that it holds as a run.
			doomed.Clear();
			newPos.Clear();
			for (int i = 0; i < n; i++)
			{
				newPos[new_path[i].idx] = i;
			}
			for (int i = 0; i < n; i++)
			{
				if (!startingWith.TryGetValue(new_path[i].idx, out List<List<SConduit>> starting))
				{
					continue;
				}
				for (int k = 0; k < starting.Count; k++)
				{
					List<SConduit> list = starting[k];
					if (n < list.Count || !newPos.TryGetValue(list[list.Count - 1].idx, out int end))
					{
						continue;
					}
					if (IsRun(new_path, i, end, list))
					{
						doomed.Add(list);
					}
				}
			}
			int before = pathList.Count;
			pathList.RemoveAll(p => p.Count <= 0 || doomed.Contains(p));
			if (pathList.Count != before)
			{
				foreach (List<SConduit> gone in doomed)
				{
					Unindex(gone);
				}
			}

			// (3) Strip each conduit a kept path holds whose permitted flow is one direction.
			if (pathList.Count > 0)
			{
				for (int i = n - 1; i >= 0; i--)
				{
					int idx = new_path[i].idx;
					if (holding.TryGetValue(idx, out HashSet<List<SConduit>> h) && h.Count > 0
						&& Mathf.IsPowerOfTwo(flow.GetSOAInfo().GetPermittedFlowDirections(idx)))
					{
						new_path.RemoveAt(i);
					}
				}
			}
			pathList.Add(new_path);
			IndexPath(new_path);
			indexedCount = pathList.Count;
		}

		/// <summary>Klei's run test: `outer[a + j] == inner[j]` for a + j below b. It never looks
		/// at position b, and b below a passes.</summary>
		private static bool IsRun(List<SConduit> outer, int a, int b, List<SConduit> inner)
		{
			for (int i = a, j = 0; i < b; i++, j++)
			{
				if (outer[i].idx != inner[j].idx)
				{
					return false;
				}
			}
			return true;
		}

		private static void Reindex(List<List<SConduit>> pathList)
		{
			if (indexed != null)
			{
				rebuilds++;
			}
			Forget();
			foreach (List<SConduit> path in pathList)
			{
				IndexPath(path);
			}
			indexed = pathList;
			indexedCount = pathList.Count;
		}

		private static void IndexPath(List<SConduit> path)
		{
			if (path.Count == 0)
			{
				return;
			}
			var pos = new Dictionary<int, int>(path.Count);
			for (int i = 0; i < path.Count; i++)
			{
				int idx = path[i].idx;
				pos[idx] = i;
				if (!holding.TryGetValue(idx, out HashSet<List<SConduit>> h))
				{
					holding[idx] = h = new HashSet<List<SConduit>>();
				}
				h.Add(path);
			}
			positions[path] = pos;
			int first = path[0].idx;
			if (!startingWith.TryGetValue(first, out List<List<SConduit>> s))
			{
				startingWith[first] = s = new List<List<SConduit>>();
			}
			s.Add(path);
		}

		private static void Unindex(List<SConduit> path)
		{
			if (!positions.Remove(path))
			{
				return;
			}
			foreach (SConduit c in path)
			{
				holding[c.idx].Remove(path);
			}
			startingWith[path[0].idx].Remove(path);
		}

		private static List<List<SConduit>> RunKlei(SolidConduitFlow flow, List<List<SConduit>> pathList, List<SConduit> copy)
		{
			// Klei's TryAdd on a shallow copy of the kept list: it never changes a kept path, only
			// the list and the new path, so sharing the path objects is safe.
			List<List<SConduit>> saved = new List<List<SConduit>>(pathList);
			inCheck = true;
			try
			{
				tryAdd.Invoke(flow, new object[] { copy });
			}
			finally
			{
				inCheck = false;
			}
			List<List<SConduit>> result = new List<List<SConduit>>(pathList);
			pathList.Clear();
			pathList.AddRange(saved);
			// Klei's appended its own copy of the new path; compare by contents.
			return result;
		}

		private static void Compare(List<List<SConduit>> expect, List<List<SConduit>> got)
		{
			string diff = null;
			if (expect.Count != got.Count)
			{
				diff = "kept " + got.Count + " paths, Klei's kept " + expect.Count;
			}
			else
			{
				for (int i = 0; i < got.Count && diff == null; i++)
				{
					List<SConduit> a = expect[i], b = got[i];
					bool last = i == got.Count - 1;
					if (!last && !ReferenceEquals(a, b))
					{
						diff = "path " + i + " is a different path";
					}
					else if (a.Count != b.Count)
					{
						diff = "path " + i + " holds " + b.Count + " conduits, Klei's " + a.Count;
					}
					else
					{
						for (int j = 0; j < a.Count; j++)
						{
							if (a[j].idx != b[j].idx)
							{
								diff = "path " + i + " differs at " + j;
								break;
							}
						}
					}
				}
			}
			if (diff != null)
			{
				mismatches++;
				if (mismatches <= 5)
				{
					PerfLog.Warn("conveyorpaths: MISMATCH at call " + calls.ToString(CultureInfo.InvariantCulture) + ": " + diff);
				}
			}
		}

		public static string Report()
		{
			if (!installed)
			{
				return null;
			}
			return string.Format(CultureInfo.InvariantCulture, "conveyorpaths: {0:N0} calls, {1:N0} reindexes{2}",
				calls, rebuilds, Check ? ", " + mismatches.ToString("N0", CultureInfo.InvariantCulture) + " mismatches against the game's" : "");
		}
	}
}
