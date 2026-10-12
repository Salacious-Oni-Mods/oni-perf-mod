using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using UnityEngine;

namespace OniPerf
{
	/// <summary>
	/// the Start() deferral. Hold back the Start() call of every loaded object
	/// more than N screens from the camera the save restores, let the loading screen go, and spawn
	/// them afterwards a few milliseconds a frame -- nearest the player's camera first.
	///
	/// Why this and not the whole object. The order Klei builds an object in is
	/// OnPrefabInit -> deserialize -> OnSpawn, and KPrefabID registers itself with KPrefabIDTracker
	/// at [OnDeserialized] (the game's KPrefabID), not at spawn. So an object whose OnSpawn
	/// has not run yet is still a resolvable target: every Ref&lt;T&gt; pointing at it finds it, and
	/// none of them takes Ref's miss path, which does not merely return null -- it sets id = -1 and
	/// orphans the link for good (the game's Ref). Deferring the whole object breaks that and needs
	/// GetInstance intercepted; deferring only the replay does not.
	///
	/// What it is worth. LoadCensus measured the replay by this exact distance on Euphoria:
	/// 3,142 of 238,932 objects are on screen when the loading screen disappears and everything
	/// they cost is 0.5 s of 45.8 s; deferring the replay for the rest prices at 18.0 s
	/// (measured). That time is not destroyed, it is moved after the loading screen, where
	/// the game is drawing frames and taking input while it is paid.
	///
	/// What a deferred object is, while it waits. Spawn() is what adds a component to
	/// SimAndRenderScheduler (KMonoBehaviour.Spawn), so a deferred object does not tick and
	/// does not draw -- it is invisible, not half-drawn. It is also absent from the indexes OnSpawn
	/// writes: Grid.Objects (OccupyArea), Grid.Foundation and the room prober
	/// (BuildingComplete), and every Components.* list a spawn registers with. The SIM
	/// is not affected at all: Sim.LoadWorld runs in the prelude, so cell mass, temperature and
	/// element are complete whatever the managed side defers.
	///
	/// The keep-list is a judgement, not a measurement. A handful of types are never deferred
	/// however far away they sit, because they are what the rest of the colony asks about rather
	/// than things sitting in the world: duplicants and their proxies, the per-world containers and
	/// inventories, the printing pod, and cluster-map entities. There are tens of them, not
	/// thousands, so keeping them costs nothing measurable and removes the least predictable
	/// failures. What else may still be inactive when the loading screen disappears is an open
	/// question this file does not answer.
	/// </summary>
	internal static class DeferredSpawn
	{
		/// <summary>Drain order is by tile, not by object: sorting a quarter of a million entries
		/// every frame is not affordable, and 16 cells is the granularity the census already showed
		/// the colony clusters at.</summary>
		private const int TileCells = 16;
		/// <summary>Tiles of slack around the visible rect, so a pan finds its objects already
		/// spawned rather than spawning them under the player's eyes.</summary>
		private const int MarginTiles = 2;

		private sealed class TileQueue
		{
			public readonly List<int> idx = new List<int>();
			public int head;
		}

		private static bool armed;
		private static float threshold;
		private static float budgetMs;
		// Replay order, and the drain preserves it within a tile: the components of one object are
		// held consecutively (the loop activates one object at a time), so they spawn in the order
		// Unity would have called them.
		private static readonly List<KMonoBehaviour> pending = new List<KMonoBehaviour>();
		private static readonly Dictionary<int, TileQueue> tiles = new Dictionary<int, TileQueue>();
		// Per type: 0 defer it, 1 kept by the list, 2 kept for its Unity tick, 3 kept because it
		// publishes itself. The reason is cached with the decision so the counters below can be
		// per COMPONENT -- counting them where the decision is computed counts types instead, and
		// the two differ by three orders of magnitude.
		private static readonly Dictionary<Type, int> keepCache = new Dictionary<Type, int>();
		private static int cursor, spawned, kept, destroyed, alreadySpawned, fromView, drainFrames;
		private static int gcFrames, gcCount, flushes;
		private static long gcFrameTicks, worstCleanFrameTicks;
		private static long drainTicks, worstFrameTicks;
		private static double beganAt;
		private static GameObject host;

		private static readonly Type[] KeepBases = { typeof(ClusterGridEntity) };
		private static readonly HashSet<Type> KeepExact = new HashSet<Type>
		{
			typeof(MinionIdentity), typeof(MinionAssignablesProxy),
			typeof(WorldContainer), typeof(WorldInventory), typeof(Telepad)
		};
		private static readonly string[] UnityTickMessages = { "Update", "LateUpdate", "FixedUpdate" };
		private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
		private static int keptByList, keptByTick, keptByStatic;

		/// <summary>True when the option is on AND a camera rect was readable. Without the rect
		/// there is no distance to decide by, so the feature switches itself off rather than
		/// deferring by some other rule.</summary>
		public static bool Armed => armed;

		/// <summary>True from the end of the load until the last held component has spawned. Any
		/// frame measurement taken while this is true contains the drain.</summary>
		public static bool Draining { get; private set; }

		public static void Arm()
		{
			armed = false;
			pending.Clear();
			tiles.Clear();
			keepCache.Clear();
			cursor = spawned = kept = destroyed = alreadySpawned = fromView = drainFrames = 0;
			keptByList = keptByTick = keptByStatic = 0;
			drainTicks = worstFrameTicks = gcFrameTicks = worstCleanFrameTicks = 0L;
			gcFrames = gcCount = flushes = 0;
			threshold = FastLoadOptions.AsyncLoadDeferScreens;
			// Never zero: the drain spends a budget per frame, and a zero budget is a coroutine
			// that yields forever without spawning anything.
			budgetMs = Mathf.Max(0.1f, FastLoadOptions.AsyncLoadDeferMs);
			if (threshold < 0f)
			{
				return;
			}
			if (!LoadRect.Armed)
			{
				PerfLog.Warn("defer: no usable camera rect, so there is no distance to defer by; every Start() runs in the load");
				return;
			}
			armed = true;
			PerfLog.Info(string.Format(CultureInfo.InvariantCulture,
				"defer: holding the Start() of everything more than {0:0.##} screens from the restored camera, to spawn afterwards at {1:0.#} ms a frame, nearest the camera first",
				threshold, budgetMs));
		}

		/// <summary>Unity ticks a component whose GameObject is active whether or not anything
		/// has called OnSpawn, because Update is its own message and knows nothing about Klei's
		/// lifecycle. So a type with one runs during the drain against fields its OnSpawn has not
		/// filled yet -- which is what BackwallSelectionObject.Update did on the test save:
		/// 45 NREs on a KSelectable its OnSpawn had not fetched. Klei's own
		/// convention keeps per-frame work in SimAndRenderScheduler, which Spawn() registers with,
		/// so these types are the rare exception: 963 of 33,635 started components on that save.</summary>
		private static bool HasUnityTick(Type t)
		{
			for (int i = 0; i < UnityTickMessages.Length; i++)
			{
				if (t.GetMethod(UnityTickMessages[i], AnyInstance, null, Type.EmptyTypes, null) != null)
				{
					return true;
				}
			}
			return false;
		}

		/// <summary>A type that parks itself in a static of its own type is one other code reaches
		/// through that static, and no such caller checks isSpawned. CellSelectionObject does it
		/// (selectionObjectA/B, set in OnSpawn) and its own static IsSelectionObject dereferences
		/// them from the hover card every frame -- 45 more NREs on the same run. 173 types in the
		/// game do this and they are singletons: 70 components on the test save.</summary>
		internal static bool PublishesItselfStatically(Type t)
		{
			for (Type at = t; at != null && at != typeof(KMonoBehaviour); at = at.BaseType)
			{
				FieldInfo[] fields = at.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
				for (int i = 0; i < fields.Length; i++)
				{
					if (fields[i].FieldType.IsAssignableFrom(t))
					{
						return true;
					}
				}
			}
			return false;
		}

		/// <summary>The keep-list alone, without the tick and static rules; DeferredObject applies it
		/// per prefab.</summary>
		internal static bool KeptByList(Type t)
		{
			if (KeepExact.Contains(t))
			{
				return true;
			}
			for (int i = 0; i < KeepBases.Length; i++)
			{
				if (KeepBases[i].IsAssignableFrom(t))
				{
					return true;
				}
			}
			return false;
		}

		private static int KeepReason(Type t)
		{
			if (keepCache.TryGetValue(t, out int reason))
			{
				return reason;
			}
			reason = 0;
			if (KeepExact.Contains(t))
			{
				reason = 1;
			}
			else
			{
				for (int i = 0; i < KeepBases.Length && reason == 0; i++)
				{
					if (KeepBases[i].IsAssignableFrom(t))
					{
						reason = 1;
					}
				}
			}
			if (reason == 0 && HasUnityTick(t))
			{
				reason = 2;
			}
			if (reason == 0 && PublishesItselfStatically(t))
			{
				reason = 3;
			}
			keepCache[t] = reason;
			return reason;
		}

		/// <summary>The position is the one the component has BEFORE its Start(), which is the only
		/// one a deferral can decide on -- OnSpawn moves things (a building snaps to its cell
		/// centre, BuildingComplete.OnSpawn).</summary>
		public static bool ShouldDefer(Type type, Vector3 pos)
		{
			if (!armed || float.IsNaN(pos.x) || float.IsNaN(pos.y))
			{
				return false;
			}
			if (LoadRect.Screens(pos) <= threshold)
			{
				return false;
			}
			int reason = KeepReason(type);
			if (reason != 0)
			{
				kept++;
				if (reason == 1) { keptByList++; }
				else if (reason == 2) { keptByTick++; }
				else { keptByStatic++; }
				return false;
			}
			return true;
		}

		private static int TileKey(int tx, int ty)
		{
			// Both fit in 16 bits for any world ONI can load (Grid dimensions are int but the
			// largest shipped asteroid is under 1,024 cells a side, i.e. 64 tiles).
			return (tx << 16) | (ty & 0xFFFF);
		}

		public static void Hold(KMonoBehaviour k, Vector3 pos)
		{
			int index = pending.Count;
			pending.Add(k);
			int key = TileKey(Mathf.FloorToInt(pos.x) / TileCells, Mathf.FloorToInt(pos.y) / TileCells);
			if (!tiles.TryGetValue(key, out TileQueue q))
			{
				tiles[key] = q = new TileQueue();
			}
			q.idx.Add(index);
		}

		/// <summary>Called once the load is over and the loading screen is gone.</summary>
		public static void Begin()
		{
			if (!armed || pending.Count == 0)
			{
				if (armed)
				{
					PerfLog.Info("defer: nothing was deferred; the whole replay ran in the load");
				}
				return;
			}
			Draining = true;
			beganAt = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
			PerfLog.Info(string.Format(CultureInfo.InvariantCulture,
				"defer: {0:N0} components held back past the loading screen, spread over {1:N0} tiles of {2} cells; draining at {3:0.#} ms a frame. {4:N0} off-screen components were kept anyway: {5:N0} by the list, {6:N0} because their type has a Unity tick, {7:N0} because their type publishes itself to a static",
				pending.Count, tiles.Count, TileCells, budgetMs, kept, keptByList, keptByTick, keptByStatic));
			host = new GameObject("OniPerf.DeferredSpawn");
			UnityEngine.Object.DontDestroyOnLoad(host);
			host.AddComponent<DeferredSpawnHost>().StartCoroutine(Drain());
		}

		/// <summary>One component, with everything that can have happened to it since the load
		/// checked. Spawn() is idempotent (KMonoBehaviour.Spawn) but an entry can also have
		/// been destroyed, and Start() on a destroyed component throws.</summary>
		private static bool SpawnOne(int index)
		{
			KMonoBehaviour k = pending[index];
			pending[index] = null;
			// The two are different failures and are counted apart: destroyed means the world got
			// rid of the object while it waited, already-spawned means something else called
			// Spawn() on it -- a Storage unpacking its contents, a chore taking an object over --
			// which is the deferral being overtaken rather than losing anything.
			if (k == null)
			{
				destroyed++;
				return false;
			}
			if (k.isSpawned)
			{
				alreadySpawned++;
				return false;
			}
			try
			{
				k.Start();
			}
			catch (Exception e)
			{
				PerfLog.Warn("defer: " + k.GetType().Name + ".Start() threw during the drain: " + e.Message);
			}
			spawned++;
			return true;
		}

		/// <summary>
		/// Prefix on SaveLoader.Save(BinaryWriter), the private method every save path funnels
		/// through (the game's SaveLoader.cs 336-344, called from the public Save at 831): finish the
		/// drain before a single byte is written.
		///
		/// Without it a save taken during the drain silently drops every object still waiting.
		/// SaveManager writes sceneObjects (SaveManager.cs 199-250), the loader creates those lists
		/// EMPTY (SaveManager.cs 322; AsyncLoad.LoadObjects does the same), and an object enters its
		/// list only from SaveLoadRoot.OnSpawn -> saveManager.Register (SaveLoadRoot.cs 48-56) --
		/// which is exactly the call this file defers. On Euphoria that is 5,794,997 of 6,052,618
		/// Start() calls, so an autosave inside the drain window (219 s at the
		/// 4 ms default) would have written a colony with almost no buildings in it. The rigs never
		/// met this because Meter and SmTrip wait for Draining to clear before they save; a player
		/// or an autosave does not.
		///
		/// Spawning everything inside the save is a hitch the size of the remaining drain, and that
		/// is the right trade: the alternative is a save that loses the colony. Not run live yet.
		/// </summary>
		internal static void FlushForSave()
		{
			// The second test: a flushed drain stays Draining until its coroutine next resumes and
			// reports, and a second save in that gap has nothing left to spawn.
			if (!Draining || cursor >= pending.Count)
			{
				return;
			}
			int before = spawned + destroyed + alreadySpawned;
			long t0 = Stopwatch.GetTimestamp();
			while (cursor < pending.Count)
			{
				int index = cursor++;
				if (!ReferenceEquals(pending[index], null))
				{
					SpawnOne(index);
				}
			}
			flushes++;
			PerfLog.Warn(string.Format(CultureInfo.InvariantCulture,
				"defer: a save started with the drain unfinished; spawned the remaining {0:N0} components first in {1:0.0} ms so the save holds the whole colony",
				spawned + destroyed + alreadySpawned - before, (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency));
		}

		private static IEnumerator Drain()
		{
			var clock = Stopwatch.StartNew();
			while (spawned + destroyed + alreadySpawned < pending.Count)
			{
				// The host is DontDestroyOnLoad, so it outlives the scene the components live in.
				// Spawning into a world that has gone back to the menu is not a partial world, it
				// is a broken one, so the drain gives up instead.
				if (Game.Instance == null || App.IsExiting)
				{
					PerfLog.Warn(string.Format(CultureInfo.InvariantCulture,
						"defer: the game went away with {0:N0} components still unspawned; drain abandoned", pending.Count - spawned - destroyed - alreadySpawned));
					break;
				}
				long frameStart = clock.ElapsedTicks;
				long budgetTicks = (long)(budgetMs * Stopwatch.Frequency / 1000.0);
				// GC is back on by the time the drain runs (AsyncLoad releases it after the
				// replay) and a collection on this heap is measured in seconds, so a frame that
				// collects has to be told apart from a frame that merely overran its budget --
				// otherwise the worst frame is attributed to the slice size and the fix goes to
				// the wrong place.
				int gcBefore = GC.CollectionCount(0);

				// 1. whatever the player is looking at, plus a margin, so a pan does not outrun the
				// drain. The rect is read fresh every frame: this is the CURRENT camera, not the
				// one the save restored.
				GridArea view = GridVisibleArea.GetVisibleArea();
				if (view.Max.x > view.Min.x && tiles.Count > 0)
				{
					int tx0 = view.Min.x / TileCells - MarginTiles, tx1 = view.Max.x / TileCells + MarginTiles;
					int ty0 = view.Min.y / TileCells - MarginTiles, ty1 = view.Max.y / TileCells + MarginTiles;
					for (int tx = tx0; tx <= tx1 && clock.ElapsedTicks - frameStart < budgetTicks; tx++)
					{
						for (int ty = ty0; ty <= ty1 && clock.ElapsedTicks - frameStart < budgetTicks; ty++)
						{
							if (!tiles.TryGetValue(TileKey(tx, ty), out TileQueue q))
							{
								continue;
							}
							while (q.head < q.idx.Count && clock.ElapsedTicks - frameStart < budgetTicks)
							{
								int index = q.idx[q.head++];
								// A slot the sequential pass already took is a null REFERENCE;
								// a destroyed component is a live reference that compares null
								// through Unity's operator, and SpawnOne counts that one.
								if (!ReferenceEquals(pending[index], null) && SpawnOne(index))
								{
									fromView++;
								}
							}
							if (q.head >= q.idx.Count)
							{
								tiles.Remove(TileKey(tx, ty));
							}
						}
					}
				}

				// 2. the rest, in the order the replay would have run them. Entries the view pass
				// already took are null here and cost one test each.
				while (cursor < pending.Count && clock.ElapsedTicks - frameStart < budgetTicks)
				{
					int index = cursor++;
					if (!ReferenceEquals(pending[index], null))
					{
						SpawnOne(index);
					}
				}
				long frame = clock.ElapsedTicks - frameStart;
				drainTicks += frame;
				if (frame > worstFrameTicks)
				{
					worstFrameTicks = frame;
				}
				int collected = GC.CollectionCount(0) - gcBefore;
				if (collected > 0)
				{
					gcFrames++;
					gcCount += collected;
					gcFrameTicks += frame;
				}
				else if (frame > worstCleanFrameTicks)
				{
					worstCleanFrameTicks = frame;
				}
				drainFrames++;
				if (cursor >= pending.Count)
				{
					// Every slot is nulled as it is taken and taken exactly once, so a finished
					// sequential sweep means a finished drain. The test is a stop, not a guard:
					// without it a miscount would spin this coroutine forever.
					break;
				}
				yield return null;
			}
			Report();
			Draining = false;
			pending.Clear();
			tiles.Clear();
			UnityEngine.Object.Destroy(host);
			host = null;
		}

		private static void Report()
		{
			double f = Stopwatch.Frequency;
			double wall = Stopwatch.GetTimestamp() / f - beganAt;
			PerfLog.Info(string.Format(CultureInfo.InvariantCulture,
				"defer: drained {0:N0} components in {1:0.0} s of wall clock over {2:N0} frames -- {3:0.0} s of main thread ({4:0.0} ms a frame, worst {5:0.0} ms); {6:N0} came from the camera's own tiles first, {7:N0} in load order; {8:N0} had been spawned by something else and {9:N0} had been destroyed before the drain reached them",
				spawned, wall, drainFrames, drainTicks / f, drainFrames > 0 ? drainTicks * 1000.0 / (drainFrames * f) : 0.0,
				worstFrameTicks * 1000.0 / f, fromView, spawned - fromView, alreadySpawned, destroyed));
			PerfLog.Info(string.Format(CultureInfo.InvariantCulture,
				"defer: of those frames {0:N0} contained a collection ({1:N0} collections, {2:0.0} s in them, {3:0.0} ms each); the worst frame that collected NOTHING was {4:0.0} ms, which is what the slice size itself costs",
				gcFrames, gcCount, gcFrameTicks / f, gcFrames > 0 ? gcFrameTicks * 1000.0 / (gcFrames * f) : 0.0,
				worstCleanFrameTicks * 1000.0 / f));
			if (flushes > 0)
			{
				PerfLog.Info("defer: " + flushes + " save(s) finished the drain early; the frame counts above stop at the first one");
			}
		}
	}

	/// <summary>Coroutine host for the drain; outlives the loader's own host.</summary>
	internal sealed class DeferredSpawnHost : MonoBehaviour
	{
	}
}
