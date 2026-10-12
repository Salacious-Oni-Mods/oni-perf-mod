using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace OniPerf
{
	/// <summary>
	/// the whole-object deferral. Do not build a loaded object at all when it
	/// sits more than N screens from the camera the save restores: keep its saved bytes, skip them in
	/// the object loop, and build it after the loading screen, a few milliseconds a frame, nearest
	/// the current camera first. The Start() deferral (DeferredSpawn.cs) holds back only Start(); this holds back
	/// KInstantiate, Awake / OnPrefabInit and the deserialize too -- the 45.3 s it was priced at.
	///
	/// Not yet run in a live game: experimental, and off unless the option is given.
	///
	/// The five parts:
	///
	/// 1. Key. ObjectRecord.Scan walks a record's bytes and reads KPrefabID.InstanceID without
	///    building anything (checked against Klei on all 238,790 Euphoria records).
	///
	/// 2. Hold. PreScan walks the whole object section ahead of the loop and decides every record by
	///    its saved position and a per-PREFAB keep rule. It has to be ahead, not inline: the batched
	///    path asks InstantiateAsync for a group's clones before the loop reaches the group, so the
	///    counts it asks for must already exclude what will be deferred, or clones are made and
	///    wasted. The loop then skips a deferred record by its stored extent and checks every kept
	///    record ends where PreScan said it would -- the in-game extent gate, on every record.
	///
	/// 3. Materialize = Klei's own SaveLoadRoot.Load over the stored bytes. The id leaves the map
	///    FIRST: KPrefabID.OnDeserializedMethod looks its own id up (KPrefabID.cs 353-362), and a
	///    still-mapped id would materialize the object again from inside itself.
	///
	/// 4. The miss hook. A prefix on KPrefabIDTracker.GetInstance materializes a mapped id before the
	///    original looks it up, so Ref&lt;T&gt;.Get never reaches its miss path (which sets id = -1 for
	///    good, Ref.cs 64-71). Installed at mod load, before any caller is JIT-compiled: GetInstance
	///    is 19 bytes of IL, under Mono's inline limit, and a caller compiled before the patch may
	///    have the original inlined past it on retail-optimized assemblies.
	///
	/// 5. Drain and save. The drain is the Start() deferral's: tiles of 16 cells around the current view first,
	///    then load order. A save during the drain materializes and spawns every remaining record
	///    first (FlushForSave), because an object enters SaveManager's lists only from
	///    SaveLoadRoot.OnSpawn and a record that was never built would simply be missing.
	///
	/// What a deferred record is, while it waits: nothing at all on the managed side. Beyond what
	/// the Start() deferral already leaves absent (Grid.Objects, the room prober, every Components list a spawn
	/// writes), it is also missing from the registrations OnPrefabInit and [OnDeserialized] make --
	/// GameComps.DiseaseContainers and StructureTemperatures above all. The sim
	/// is complete either way; Sim.LoadWorld ran in the prelude.
	///
	/// Memory: the decompressed save body is kept alive until the drain ends, because every record
	/// is an offset into it. That is the cheap way, not the small way; measuring it is owed.
	/// </summary>
	internal static class DeferredObject
	{
		private const int TileCells = 16;
		private const int MarginTiles = 2;

		private sealed class Record
		{
			public GameObject prefab;
			public string name;
			public int start, end, id;
			public bool done;
		}

		private sealed class TileQueue
		{
			public readonly List<int> idx = new List<int>();
			public int head;
		}

		private static bool installed, armed;
		private static float threshold, budgetMs;
		private static byte[] body;
		// The save's KPrefabID template as PreScan saw it. Every record is read against the save's
		// templates; KSerialization.Manager.Clear() (SaveLoader.Save(string) does one) replaces them
		// with the current type layouts, and a record read that way comes out short and broken.
		private static KSerialization.DeserializationTemplate kpidTemplate;
		private static int templateLost;
		// Load order. The drain's sequential pass walks it with `cursor`.
		private static readonly List<Record> records = new List<Record>();
		private static readonly Dictionary<int, Record> byId = new Dictionary<int, Record>();
		private static readonly Dictionary<int, TileQueue> tiles = new Dictionary<int, TileQueue>();
		// Per record of the object section, in load order: the deferred record or null, and where
		// the record ends. Both are what the loop checks itself against.
		private static Record[] plan;
		private static int[] ends;
		// Built objects whose components have not been started yet: the hook builds an object in
		// the middle of someone else's code, so starting it there is not safe; the drain (or a
		// save) starts it at the next opportunity, and Unity's own Start() call is idempotent with
		// that (KMonoBehaviour.cs 116-120).
		private static readonly List<GameObject> toStart = new List<GameObject>();
		// Per prefab: 0 defer it, 1 kept by the Start() deferral's list, 2 kept because a component publishes
		// itself to a static, 3 kept as gameplay-facing (Pickupable, Crop, Edible, Health,
		// Capturable), 4 kept by name.
		private static readonly Dictionary<GameObject, int> prefabKeep = new Dictionary<GameObject, int>();

		private static int cursor, built, fromHook, fromView, fromFlush, buildFailed, extentWarnings, drainFrames, flushes;
		private static int keptNear, keptByList, keptByStatic, keptByGameplay, keptByName, keptNoPrefab, keptDuplicateId;
		private static long scanTicks, drainTicks, worstFrameTicks;
		// Where the drain's main thread goes: Klei's SaveLoadRoot.Load, the Start() pass, and the
		// single slowest record (Load + Start), so a frame that blows its budget can be named.
		private static long loadTicks, startTicks, worstRecordTicks;
		private static string worstRecordName;
		private static int drainGcBefore;
		private static bool drainGcHeld;
		// InstantiateAsync for the drain, the loop's batching (AsyncLoad.Batcher) turned into a pool:
		// runs of one prefab ahead of the cursor are cloned off the main thread, and whoever builds
		// a record of that prefab next -- the cursor, the camera's tiles or a lookup -- takes a clone
		// instead of instantiating one. A record built before its clone arrived costs one spare
		// clone, destroyed at the end.
		private static readonly Dictionary<GameObject, Stack<GameObject>> pool = new Dictionary<GameObject, Stack<GameObject>>();
		private static readonly Queue<KeyValuePair<GameObject, AsyncInstantiateOperation<GameObject>>> pending = new Queue<KeyValuePair<GameObject, AsyncInstantiateOperation<GameObject>>>();
		private static int issueAt, outstanding, clonesIssued, clonesUsed;
		private static bool drainBatching;
		private static float integrationWas;
		private static long drainInst0, drainAct0, drainDeser0;
		private static double beganAt;
		private static GameObject host;
		private static readonly Stopwatch Clock = Stopwatch.StartNew();

		/// <summary>The objects dupes fetch, eat, farm, heal or capture. Deferring one hides it from
		/// the errand system until the drain reaches it -- a colony that "forgets" its food for a
		/// minute is a visible defect, not a perf trade, so these load normally unless
		/// the whole-object deferral-all says otherwise. About 10,500 of Euphoria's 238,790
		/// objects.</summary>
		private static readonly Type[] GameplayKeep = { typeof(Pickupable), typeof(Crop), typeof(Edible), typeof(Health), typeof(Capturable) };
		/// <summary>The game's own state object. The static-publisher rule already keeps it
		/// (SaveGame.Instance); naming it as well costs nothing, and losing it would be a broken
		/// world rather than a late one.</summary>
		private static readonly HashSet<string> KeepNames = new HashSet<string> { "SaveGame" };

		public static bool Armed => armed;

		/// <summary>True from the end of the load until the last deferred record is built and
		/// started. Measurements and digests wait on it, as they wait on DeferredSpawn.Draining.</summary>
		public static bool Draining { get; private set; }

		/// <summary>At mod load. The GetInstance prefix must exist before anything that calls
		/// GetInstance is JIT-compiled (see the class summary); without it the feature does not run.</summary>
		public static void Install(Harmony harmony)
		{
			installed = false;
			if (FastLoadOptions.AsyncLoadDeferObjectScreens < 0f)
			{
				return;
			}
			MethodInfo getInstance = AccessTools.Method(typeof(KPrefabIDTracker), nameof(KPrefabIDTracker.GetInstance), new[] { typeof(int) });
			if (getInstance == null)
			{
				PerfLog.Warn("defer-object: no KPrefabIDTracker.GetInstance(int); whole-object deferral switched OFF, because a deferred object nothing can find is a lost one");
				FastLoadOptions.AsyncLoadDeferObjectScreens = -1f;
				return;
			}
			harmony.Patch(getInstance, prefix: new HarmonyMethod(typeof(DeferredObject), nameof(GetInstancePrefix)));
			installed = true;
			PerfLog.Info("defer-object: KPrefabIDTracker.GetInstance hooked at mod load");
		}

		/// <summary>Once per load, in the prelude. Clears whatever a previous load left.</summary>
		public static void Arm()
		{
			Abandon(null);
			armed = false;
			prefabKeep.Clear();
			cursor = built = fromHook = fromView = fromFlush = buildFailed = extentWarnings = drainFrames = flushes = 0;
			keptNear = keptByList = keptByStatic = keptByGameplay = keptByName = keptNoPrefab = keptDuplicateId = 0;
			scanTicks = drainTicks = worstFrameTicks = 0L;
			loadTicks = startTicks = worstRecordTicks = 0L;
			worstRecordName = null;
			threshold = FastLoadOptions.AsyncLoadDeferObjectScreens;
			budgetMs = Mathf.Max(0.1f, FastLoadOptions.AsyncLoadDeferMs);
			if (threshold < 0f || !installed)
			{
				return;
			}
			if (!LoadRect.Armed)
			{
				PerfLog.Warn("defer-object: no usable camera rect, so there is no distance to defer by; every object is built in the load");
				return;
			}
			armed = true;
			PerfLog.Info(string.Format(CultureInfo.InvariantCulture,
				"defer-object: building nothing more than {0:0.##} screens from the restored camera during the load; it is built afterwards at {1:0.#} ms a frame, nearest the camera first{2}",
				threshold, budgetMs, FastLoadOptions.AsyncLoadDeferObjectAll ? "; gameplay-facing objects (pickupables, crops, edibles, health, capturables) are deferred too" : ""));
		}

		private static int KeepReason(GameObject prefab)
		{
			if (prefabKeep.TryGetValue(prefab, out int reason))
			{
				return reason;
			}
			reason = 0;
			if (KeepNames.Contains(prefab.name))
			{
				reason = 4;
			}
			else
			{
				// Children too: a prefab's KMonoBehaviours are not all on its root.
				Component[] comps = prefab.GetComponentsInChildren<Component>(true);
				for (int i = 0; i < comps.Length && reason == 0; i++)
				{
					if (comps[i] == null)
					{
						continue;
					}
					Type t = comps[i].GetType();
					// The Start() deferral's Unity-tick rule is deliberately not here: a deferred record is never
					// instantiated, so nothing of it can tick early.
					if (DeferredSpawn.KeptByList(t))
					{
						reason = 1;
					}
					else if (DeferredSpawn.PublishesItselfStatically(t))
					{
						reason = 2;
					}
					else if (!FastLoadOptions.AsyncLoadDeferObjectAll)
					{
						for (int g = 0; g < GameplayKeep.Length; g++)
						{
							if (GameplayKeep[g].IsAssignableFrom(t))
							{
								reason = 3;
								break;
							}
						}
					}
				}
			}
			prefabKeep[prefab] = reason;
			return reason;
		}

		private static int TileKey(int tx, int ty)
		{
			return (tx << 16) | (ty & 0xFFFF);
		}

		/// <summary>
		/// Walk the object section from <paramref name="at"/> (the group count, where
		/// AsyncLoad.PlanGroups starts too) and decide every record. <paramref name="keptPlan"/> is
		/// <paramref name="plan"/> with each count reduced to the records that will be built in the
		/// loop. False, with everything cleared and the feature off for this load, on any walk that
		/// does not reconcile: a plan the loop cannot trust is worse than no deferral.
		/// </summary>
		public static bool PreScan(byte[] b, int at, Dictionary<Tag, GameObject> prefabMap, List<KeyValuePair<string, int>> plan, out List<KeyValuePair<string, int>> keptPlan)
		{
			keptPlan = plan;
			if (!armed)
			{
				return false;
			}
			if (!ObjectRecord.Prepare())
			{
				PerfLog.Warn("defer-object: " + ObjectRecord.Why + "; every object is built in the load");
				armed = false;
				return false;
			}
			kpidTemplate = KSerialization.Manager.GetDeserializationTemplate("KPrefabID");
			templateLost = 0;
			long t0 = Clock.ElapsedTicks;
			var kept = new List<KeyValuePair<string, int>>(plan.Count);
			try
			{
				int total = 0;
				foreach (var g in plan)
				{
					total += g.Value;
				}
				var planArr = new Record[total];
				var endArr = new int[total];
				int p = at;
				int groups = BitConverter.ToInt32(b, p);
				p += 4;
				if (groups != plan.Count)
				{
					throw new FormatException("the group table says " + groups + " groups and the plan has " + plan.Count);
				}
				LoadRect.SetWorlds(ScanWorlds(b, p, plan, prefabMap));
				int r = 0;
				for (int j = 0; j < groups; j++)
				{
					int nameLen = BitConverter.ToInt32(b, p);
					p += 4;
					p += Math.Max(nameLen, 0);
					int count = BitConverter.ToInt32(b, p);
					int bytes = BitConverter.ToInt32(b, p + 4);
					p += 8;
					int groupEnd = p + bytes;
					if (count != plan[j].Value || r + count > total)
					{
						throw new FormatException("group " + j + " (" + plan[j].Key + ") holds " + count + " records and the plan says " + plan[j].Value);
					}
					string name = plan[j].Key;
					prefabMap.TryGetValue(TagManager.Create(name ?? string.Empty), out GameObject prefab);
					if (prefab == null)
					{
						// The loop skips the whole group by its byte count and never looks at a
						// record, so neither does this. Its end entries are never read.
						keptNoPrefab += count;
						r += count;
						p = groupEnd;
						kept.Add(new KeyValuePair<string, int>(name, count));
						continue;
					}
					int keepHere = 0;
					for (int k = 0; k < count; k++, r++)
					{
						int recordAt = p;
						var pos = new Vector3(BitConverter.ToSingle(b, p), BitConverter.ToSingle(b, p + 4), BitConverter.ToSingle(b, p + 8));
						p = ObjectRecord.Scan(b, recordAt, groupEnd, out int id, out _);
						endArr[r] = p;
						bool defer = false;
						if (!float.IsNaN(pos.x) && !float.IsNaN(pos.y))
						{
							if (LoadRect.Screens(pos) <= threshold)
							{
								keptNear++;
							}
							else
							{
								switch (KeepReason(prefab))
								{
									case 0: defer = true; break;
									case 1: keptByList++; break;
									case 2: keptByStatic++; break;
									case 3: keptByGameplay++; break;
									default: keptByName++; break;
								}
							}
						}
						if (defer && id != -1 && byId.ContainsKey(id))
						{
							// No save measured has one, but two records with one
							// id would make the hook build the wrong one; the second loads normally
							// and KPrefabID's own conflict path decides as it always has.
							keptDuplicateId++;
							defer = false;
						}
						if (!defer)
						{
							keepHere++;
							continue;
						}
						var rec = new Record { prefab = prefab, name = name, start = recordAt, end = p, id = id };
						planArr[r] = rec;
						if (id != -1)
						{
							byId[id] = rec;
						}
						int index = records.Count;
						records.Add(rec);
						int key = TileKey(Mathf.FloorToInt(pos.x) / TileCells, Mathf.FloorToInt(pos.y) / TileCells);
						if (!tiles.TryGetValue(key, out TileQueue q))
						{
							tiles[key] = q = new TileQueue();
						}
						q.idx.Add(index);
					}
					if (p != groupEnd)
					{
						throw new FormatException("group " + j + " (" + name + ") ends at " + p + ", its byte count says " + groupEnd);
					}
					kept.Add(new KeyValuePair<string, int>(name, keepHere));
				}
				ends = endArr;
				DeferredObject.plan = planArr;
				body = b;
			}
			catch (Exception e)
			{
				PerfLog.Warn("defer-object: the object section did not reconcile ahead of the loop (" + e.Message + "); every object is built in the load");
				Abandon(null);
				armed = false;
				return false;
			}
			scanTicks = Clock.ElapsedTicks - t0;
			keptPlan = kept;
			PerfLog.Info(string.Format(CultureInfo.InvariantCulture,
				"defer-object: scanned the object section in {0:0.0} ms; {1:N0} records deferred over {2:N0} tiles. Built in the load: {3:N0} near the camera, {4:N0} kept by the list, {5:N0} publish themselves to a static, {6:N0} gameplay-facing, {7:N0} by name, {8:N0} with no prefab, {9:N0} duplicate ids",
				scanTicks * 1000.0 / Stopwatch.Frequency, records.Count, tiles.Count, keptNear, keptByList, keptByStatic, keptByGameplay, keptByName, keptNoPrefab, keptDuplicateId));
			return true;
		}

		/// <summary>Every saved world's cell rect, from the records of prefabs that carry a
		/// WorldContainer (a handful: asteroids and rocket interiors); every other group is skipped
		/// by its byte count. <paramref name="p"/> is the first group's name length; the names are the plan's.</summary>
		private static List<RectInt> ScanWorlds(byte[] b, int p, List<KeyValuePair<string, int>> plan, Dictionary<Tag, GameObject> prefabMap)
		{
			var worlds = new List<RectInt>();
			for (int j = 0; j < plan.Count; j++)
			{
				int nameLen = BitConverter.ToInt32(b, p);
				p += 4;
				p += Math.Max(nameLen, 0);
				int count = BitConverter.ToInt32(b, p);
				int bytes = BitConverter.ToInt32(b, p + 4);
				p += 8;
				int groupEnd = p + bytes;
				prefabMap.TryGetValue(TagManager.Create(plan[j].Key ?? string.Empty), out GameObject prefab);
				if (prefab != null && prefab.GetComponent<WorldContainer>() != null)
				{
					int q = p;
					for (int k = 0; k < count; k++)
					{
						int recordAt = q;
						q = ObjectRecord.Scan(b, recordAt, groupEnd, out _, out _);
						if (ObjectRecord.TryReadWorldRect(b, recordAt, q, out RectInt w))
						{
							worlds.Add(w);
						}
					}
				}
				p = groupEnd;
			}
			return worlds;
		}

		/// <summary>The loop's question at record <paramref name="index"/> (load order, counting
		/// records of skipped groups too): -1 to build it, else where its bytes end.</summary>
		public static int DeferredEnd(int index, int position)
		{
			Record r = plan[index];
			if (r == null)
			{
				return -1;
			}
			if (position != r.start)
			{
				throw new FormatException("deferred record " + index + " (" + r.name + ") was planned at " + r.start + " and the loop is at " + position);
			}
			return r.end;
		}

		/// <summary>Where PreScan says record <paramref name="index"/> ends; the loop compares its
		/// reader against it after every object it builds.</summary>
		public static int ExpectedEnd(int index) => ends[index];

		private static void GetInstancePrefix(int instance_id)
		{
			if (byId.Count == 0)
			{
				return;
			}
			if (byId.TryGetValue(instance_id, out Record r))
			{
				fromHook++;
				Materialize(r, true);
			}
		}

		/// <summary><paramref name="queueStart"/>: the hook builds an object in the middle of someone
		/// else's code, so its Start() is queued for the drain; the drain and a flush start what they
		/// build themselves, straight away.</summary>
		private static GameObject Materialize(Record r, bool queueStart)
		{
			// First, before anything can look the id up (the class summary, point 3).
			if (r.id != -1)
			{
				byId.Remove(r.id);
			}
			r.done = true;
			if (Game.Instance == null || body == null)
			{
				return null;
			}
			if (KSerialization.Manager.GetDeserializationTemplate("KPrefabID") != kpidTemplate)
			{
				// Building it now would read it wrong. Refusing loses it, loudly; neither is a
				// pass, and SaveFlushPrefix sits ahead of the save's Clear() so this never fires.
				if (templateLost++ == 0)
				{
					PerfLog.Warn("defer-object: ERROR the save's deserialization templates are gone (KSerialization.Manager.Clear ran during the drain); unbuilt records are being LOST");
				}
				buildFailed++;
				return null;
			}
			SaveLoadRoot root = null;
			try
			{
				var reader = new FastReader(body) { Position = r.start };
				long l0 = Clock.ElapsedTicks;
				root = AsyncLoad.LoadDeferred(r.prefab, r.name, reader, TakeClone(r.prefab));
				loadTicks += Clock.ElapsedTicks - l0;
				if (reader.Position != r.end && extentWarnings++ < 20)
				{
					PerfLog.Warn("defer-object: building " + r.name + " read to " + reader.Position + ", its record ends at " + r.end);
				}
			}
			catch (Exception e)
			{
				PerfLog.Warn("defer-object: building " + r.name + " threw: " + e.Message);
			}
			if (root == null)
			{
				buildFailed++;
				return null;
			}
			built++;
			GameObject go = root.gameObject;
			// While the load is still running AsyncLoad holds every Start() and replays it in order;
			// afterwards nothing does, so the drain or a save starts it.
			if (queueStart && !AsyncLoad.Busy)
			{
				toStart.Add(go);
			}
			return go;
		}

		private static void StartObject(GameObject go)
		{
			if (go == null)
			{
				return;
			}
			long s0 = Clock.ElapsedTicks;
			try
			{
				StartComponents(go);
			}
			finally
			{
				startTicks += Clock.ElapsedTicks - s0;
			}
		}

		private static void StartComponents(GameObject go)
		{
			KMonoBehaviour[] ks = go.GetComponentsInChildren<KMonoBehaviour>();
			for (int i = 0; i < ks.Length; i++)
			{
				KMonoBehaviour k = ks[i];
				if (k == null || k.isSpawned)
				{
					continue;
				}
				try
				{
					k.Start();
				}
				catch (Exception e)
				{
					PerfLog.Warn("defer-object: " + k.GetType().Name + ".Start() threw: " + e.Message);
				}
			}
		}

		/// <summary>Count is read live: a Start() that looks up a deferred id queues one more, and it
		/// is started in the same pass.</summary>
		private static void StartPending()
		{
			for (int i = 0; i < toStart.Count; i++)
			{
				StartObject(toStart[i]);
			}
			toStart.Clear();
		}

		/// <summary>After the load, after DeferredSpawn.Begin.</summary>
		public static void Begin()
		{
			plan = null;
			ends = null;
			if (!armed)
			{
				return;
			}
			if (records.Count == 0 || built + buildFailed >= records.Count)
			{
				PerfLog.Info("defer-object: nothing left to build after the load");
				Abandon(null);
				return;
			}
			Draining = true;
			beganAt = Clock.ElapsedTicks / (double)Stopwatch.Frequency;
			PerfLog.Info(string.Format(CultureInfo.InvariantCulture,
				"defer-object: {0:N0} records to build after the loading screen ({1:N0} already built by a lookup during the load); draining at {2:0.#} ms a frame",
				records.Count - built - buildFailed, fromHook, budgetMs));
			LoadNotice.Show(built + buildFailed, records.Count);
			DrainPause.Hold();
			DrainQuiet.Begin();
			host = new GameObject("OniPerf.DeferredObject");
			UnityEngine.Object.DontDestroyOnLoad(host);
			host.AddComponent<DeferredSpawnHost>().StartCoroutine(Drain());
		}

		/// <summary>Prefix on SaveLoader.Save(BinaryWriter), before DeferredSpawn's: build and start
		/// every record still waiting, so the save holds the whole colony. See DeferredSpawn.FlushForSave
		/// for why a save would otherwise lose them.</summary>
		internal static void FlushForSave()
		{
			if (!Draining)
			{
				return;
			}
			long t0 = Clock.ElapsedTicks;
			int before = built + buildFailed;
			StartPending();
			for (; cursor < records.Count; cursor++)
			{
				Record r = records[cursor];
				if (!r.done)
				{
					fromFlush++;
					StartObject(Materialize(r, false));
				}
			}
			StartPending();
			flushes++;
			PerfLog.Warn(string.Format(CultureInfo.InvariantCulture,
				"defer-object: a save started with {0:N0} records unbuilt; built and started them first in {1:0.0} ms so the save holds the whole colony",
				built + buildFailed - before, (Clock.ElapsedTicks - t0) * 1000.0 / Stopwatch.Frequency));
		}

		private static GameObject TakeClone(GameObject prefab)
		{
			if (pool.TryGetValue(prefab, out Stack<GameObject> s) && s.Count > 0)
			{
				outstanding--;
				clonesUsed++;
				return s.Pop();
			}
			return null;
		}

		/// <summary>Takes in finished clones, then issues InstantiateAsync for the next runs of one
		/// prefab ahead of the cursor, up to AsyncLoad's lookahead in clones not yet used.</summary>
		private static void PumpClones()
		{
			while (pending.Count > 0 && pending.Peek().Value.isDone)
			{
				var p = pending.Dequeue();
				if (!pool.TryGetValue(p.Key, out Stack<GameObject> s))
				{
					pool[p.Key] = s = new Stack<GameObject>();
				}
				foreach (GameObject go in p.Value.Result)
				{
					if (go != null)
					{
						s.Push(go);
					}
					else
					{
						outstanding--;
					}
				}
			}
			if (issueAt < cursor)
			{
				issueAt = cursor;
			}
			int chunk = AsyncLoad.DrainBatchChunk, min = AsyncLoad.DrainBatchMin, lookahead = AsyncLoad.DrainBatchLookahead;
			while (issueAt < records.Count && outstanding < lookahead)
			{
				Record r = records[issueAt];
				if (r.done)
				{
					issueAt++;
					continue;
				}
				int run = 1;
				while (issueAt + run < records.Count && run < chunk && records[issueAt + run].prefab == r.prefab && !records[issueAt + run].done)
				{
					run++;
				}
				issueAt += run;
				if (run < min || !AsyncLoad.DrainBatchable(r.prefab, run))
				{
					continue;
				}
				pending.Enqueue(new KeyValuePair<GameObject, AsyncInstantiateOperation<GameObject>>(r.prefab, UnityEngine.Object.InstantiateAsync(r.prefab, run)));
				outstanding += run;
				clonesIssued += run;
			}
		}

		/// <summary>Cancels what is still cloning and destroys clones nobody took.</summary>
		private static int DropClones()
		{
			int spare = 0;
			while (pending.Count > 0)
			{
				AsyncInstantiateOperation<GameObject> op = pending.Dequeue().Value;
				if (!op.isDone)
				{
					op.Cancel();
				}
				else if (op.Result != null)
				{
					foreach (GameObject go in op.Result)
					{
						if (go != null)
						{
							UnityEngine.Object.Destroy(go);
							spare++;
						}
					}
				}
			}
			foreach (Stack<GameObject> s in pool.Values)
			{
				foreach (GameObject go in s)
				{
					if (go != null)
					{
						UnityEngine.Object.Destroy(go);
						spare++;
					}
				}
			}
			pool.Clear();
			outstanding = 0;
			issueAt = 0;
			if (drainBatching)
			{
				drainBatching = false;
				AsyncInstantiateOperation.SetIntegrationTimeMS(integrationWas);
			}
			return spare;
		}

		// FastLoadOptions.AsyncLoadDeferFps: the slice is steered so the whole frame (the drain's slice
		// plus the game's own frame, measured start to start) sits at the target. A proportional step
		// on a smoothed frame time; the game's part is not separated out because under vsync it
		// includes the wait, and the step handles that by growing until frames run over.
		private static double sliceMs = -1, smoothFrameMs = -1, sliceSum;
		private static long lastFrameStart = -1, slicedFrames, framesOver, idleFrames;
		private static bool wasIdle;

		// FastLoadOptions.AsyncLoadDeferIdleSec: the frame-rate target holds only while the player can see
		// it matter. Paused with no key, mouse, scroll or camera change for that long, the drain steers
		// to FastLoadOptions.AsyncLoadDeferIdleFps instead, or takes its whole budget when that is 0
		// (measured: at 60 fps the paused game frame leaves the drain ~6 ms a frame; at
		// 30 fps the Euphoria drain takes 87.6 s; below 30 fps was ruled out on looks).
		private static double lastInputAt = -1;
		private static Vector3 lastMouse, lastCamPos;
		private static float lastCamSize;

		private static bool Idle()
		{
			double idleSec = FastLoadOptions.AsyncLoadDeferIdleSec;
			if (idleSec <= 0)
			{
				return false;
			}
			double now = Clock.ElapsedTicks / (double)Stopwatch.Frequency;
			Vector3 mouse = Input.mousePosition;
			Camera cam = Camera.main;
			Vector3 camPos = cam != null ? cam.transform.position : Vector3.zero;
			float camSize = cam != null ? cam.orthographicSize : 0f;
			bool input = Input.anyKey || Input.mouseScrollDelta != Vector2.zero || mouse != lastMouse || camPos != lastCamPos || camSize != lastCamSize;
			lastMouse = mouse;
			lastCamPos = camPos;
			lastCamSize = camSize;
			bool paused = SpeedControlScreen.Instance != null && SpeedControlScreen.Instance.IsPaused;
			if (input || !paused || lastInputAt < 0)
			{
				lastInputAt = now;
				return false;
			}
			return now - lastInputAt >= idleSec;
		}

		private static double Slice(long frameStart)
		{
			double fps = FastLoadOptions.AsyncLoadDeferFps;
			if (fps <= 0)
			{
				return budgetMs;
			}
			bool idle = Idle();
			if (idle != wasIdle)
			{
				// The target changed: the controller starts over from half the new one, so a player
				// who touches the game gets the interactive rate the next frame, not after the
				// smoothing has caught up.
				wasIdle = idle;
				sliceMs = -1;
				smoothFrameMs = -1;
				lastFrameStart = -1;
			}
			if (idle)
			{
				idleFrames++;
				if (FastLoadOptions.AsyncLoadDeferIdleFps <= 0)
				{
					// Nobody is watching the frame rate: the whole budget.
					return budgetMs;
				}
				fps = FastLoadOptions.AsyncLoadDeferIdleFps;
			}
			double target = 1000.0 / fps;
			if (sliceMs < 0)
			{
				sliceMs = Math.Min(budgetMs, Math.Max(1.0, target / 2));
			}
			if (lastFrameStart >= 0)
			{
				double frameMs = (frameStart - lastFrameStart) * 1000.0 / Stopwatch.Frequency;
				if (frameMs > target * 1.1)
				{
					framesOver++;
				}
				smoothFrameMs = smoothFrameMs < 0 ? frameMs : smoothFrameMs * 0.7 + frameMs * 0.3;
				sliceMs = Math.Min(budgetMs, Math.Max(1.0, sliceMs + 0.5 * (target - smoothFrameMs)));
			}
			lastFrameStart = frameStart;
			slicedFrames++;
			sliceSum += sliceMs;
			return sliceMs;
		}

		private static void BuildTimed(Record r)
		{
			long t0 = Clock.ElapsedTicks;
			StartObject(Materialize(r, false));
			long t = Clock.ElapsedTicks - t0;
			if (t > worstRecordTicks)
			{
				worstRecordTicks = t;
				worstRecordName = r.name;
			}
		}


		private static long allocBuild, allocStart, allocPump, allocOutside, allocLastEnd;

		// FastLoadOptions.AsyncLoadDeferObjectNetSec: Klei's utility-network managers whose dirty flag
		// the drain is holding back, and when it next hands them over.
		private static List<object> netMgrs;
		private static FieldInfo[] netDirty;
		private static bool[] netHeld;
		private static long netReleaseAt, netRebuilds, netHolds;

		/// <summary>End of a drain frame: a manager the frame's builds marked dirty is cleared and
		/// remembered, so Game.LateUpdate now and Game.Update next frame skip the full rebuild;
		/// while unpaused, once every netsec seconds every remembered flag is set again and Klei
		/// rebuilds as usual. Euphoria: once a second still cost ~10 s of drain and 5.7 GB.
		/// The managers are found by type, every Game field that is an IUtilityNetworkMgr (gas,
		/// liquid, solid conduit, wire, logic wire, travel tube), so none is missed by name.</summary>
		private static void HoldNetworks()
		{
			if (FastLoadOptions.AsyncLoadDeferObjectNetSec <= 0 || Game.Instance == null)
			{
				return;
			}
			if (netMgrs == null)
			{
				netMgrs = new List<object>();
				var fields = new List<FieldInfo>();
				foreach (FieldInfo f in typeof(Game).GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
				{
					if (!typeof(IUtilityNetworkMgr).IsAssignableFrom(f.FieldType) || !(f.GetValue(Game.Instance) is object mgr) || netMgrs.Contains(mgr))
					{
						continue;
					}
					FieldInfo dirty = mgr.GetType().GetField("dirty", BindingFlags.Instance | BindingFlags.NonPublic);
					if (dirty == null || dirty.FieldType != typeof(bool))
					{
						PerfLog.Warn("defer-object: network manager " + f.Name + " has no bool dirty field; its rebuilds are not held");
						continue;
					}
					netMgrs.Add(mgr);
					fields.Add(dirty);
				}
				netDirty = fields.ToArray();
				netHeld = new bool[netDirty.Length];
				netReleaseAt = Clock.ElapsedTicks + (long)(FastLoadOptions.AsyncLoadDeferObjectNetSec * Stopwatch.Frequency);
				PerfLog.Info("defer-object: holding the rebuilds of " + netMgrs.Count + " utility-network managers to one per " + FastLoadOptions.AsyncLoadDeferObjectNetSec.ToString(CultureInfo.InvariantCulture) + " s during the drain");
			}
			// Paused, nothing consumes a network (no sim step, no conduit flow), so the hold lasts
			// as long as the pause; unpaused, the networks catch up once every netsec seconds.
			bool release = Time.timeScale != 0f && Clock.ElapsedTicks >= netReleaseAt;
			for (int i = 0; i < netMgrs.Count; i++)
			{
				bool dirty = (bool)netDirty[i].GetValue(netMgrs[i]);
				if (release)
				{
					if (netHeld[i] && !dirty)
					{
						netDirty[i].SetValue(netMgrs[i], true);
					}
					if (netHeld[i] || dirty)
					{
						netRebuilds++;
					}
					netHeld[i] = false;
				}
				else if (dirty)
				{
					netDirty[i].SetValue(netMgrs[i], false);
					netHeld[i] = true;
					netHolds++;
				}
			}
			if (release)
			{
				netReleaseAt = Clock.ElapsedTicks + (long)(FastLoadOptions.AsyncLoadDeferObjectNetSec * Stopwatch.Frequency);
			}
		}

		/// <summary>Hands every held dirty flag back, so the next Game.Update rebuilds exactly the
		/// networks Klei would have. Runs on every way out of the drain.
		///
		/// Only the flag goes back; the rebuild is left to Klei. CircuitManager.Refresh and
		/// LogicCircuitManager poll the manager's IsDirty and rebuild their own per-circuit tables
		/// only when they see it set. This used to call the manager's Update() here, to time the
		/// rebuild, which cleared the flag before either poller saw it: on the "Some light torture"
		/// save the wire networks of 1,321 drained records got ids past the end of
		/// CircuitManager.circuitInfo, and the first unpaused tick threw 19,429
		/// ArgumentOutOfRangeExceptions from CircuitManager.HasConsumers. Every earlier check
		/// read the colony paused, where no generator asks.</summary>
		private static void ReleaseNetworks()
		{
			if (netMgrs == null)
			{
				return;
			}
			int handed = 0;
			for (int i = 0; i < netMgrs.Count; i++)
			{
				if (netHeld[i])
				{
					netDirty[i].SetValue(netMgrs[i], true);
					netHeld[i] = false;
					handed++;
				}
			}
			PerfLog.Info(string.Format(CultureInfo.InvariantCulture, "defer-object: network rebuilds held {0:N0} times, let through {1:N0}; {2} flags handed back at the end",
				netHolds, netRebuilds, handed));
			netMgrs = null;
			netDirty = null;
			netHeld = null;
			netHolds = netRebuilds = 0;
		}

		private static IEnumerator Drain()
		{
			allocBuild = allocStart = allocPump = allocOutside = 0;
			allocLastEnd = -1;
			drainGcBefore = GC.CollectionCount(0);
			if (FastLoadOptions.AsyncLoadDeferObjectNoGc)
			{
				UnityEngine.Scripting.GarbageCollector.GCMode = UnityEngine.Scripting.GarbageCollector.Mode.Disabled;
				drainGcHeld = true;
			}
			AsyncLoad.PhaseTicks(out drainInst0, out drainAct0, out drainDeser0);
			// The loop has logged its own deserialize split by now; these count the drain's.
			FastLoadInternal.LookupTicks = FastLoadInternal.ComponentsTicks = FastLoadInternal.ManagerTicks = 0;
			FastLoadInternal.MappingTicks = FastLoadInternal.DetailsTicks = FastLoadInternal.NestedCalls = 0;
			FastLoadInternal.ByComponent.Clear();
			FastDeser.ResetCounters();
			AsyncLoad.ResetPrefabCost();
			clonesIssued = clonesUsed = 0;
			if (!FastLoadOptions.AsyncLoadNoBatch)
			{
				drainBatching = true;
				integrationWas = AsyncInstantiateOperation.GetIntegrationTimeMS();
				AsyncInstantiateOperation.SetIntegrationTimeMS(AsyncLoad.DrainIntegrationMs);
			}
			while (true)
			{
				if (Game.Instance == null || App.IsExiting)
				{
					Abandon("the game went away");
					yield break;
				}
				long frameStart = Clock.ElapsedTicks;
				long budgetTicks = (long)(Slice(frameStart) * Stopwatch.Frequency / 1000.0);
				// Allocation split (collector held, so GetTotalMemory only rises inside a frame):
				// the game between drain frames, Start(), the clone pump, and the builds.
				long m0 = GC.GetTotalMemory(false);
				if (allocLastEnd >= 0)
				{
					allocOutside += m0 - allocLastEnd;
				}
				DrainCoalesce.Begin();
				StartPending();
				long m1 = GC.GetTotalMemory(false);
				allocStart += m1 - m0;
				if (drainBatching)
				{
					PumpClones();
				}
				long m2 = GC.GetTotalMemory(false);
				allocPump += m2 - m1;

				GridArea view = GridVisibleArea.GetVisibleArea();
				if (view.Max.x > view.Min.x && tiles.Count > 0)
				{
					int tx0 = view.Min.x / TileCells - MarginTiles, tx1 = view.Max.x / TileCells + MarginTiles;
					int ty0 = view.Min.y / TileCells - MarginTiles, ty1 = view.Max.y / TileCells + MarginTiles;
					for (int tx = tx0; tx <= tx1 && Clock.ElapsedTicks - frameStart < budgetTicks; tx++)
					{
						for (int ty = ty0; ty <= ty1 && Clock.ElapsedTicks - frameStart < budgetTicks; ty++)
						{
							int key = TileKey(tx, ty);
							if (!tiles.TryGetValue(key, out TileQueue q))
							{
								continue;
							}
							while (q.head < q.idx.Count && Clock.ElapsedTicks - frameStart < budgetTicks)
							{
								Record r = records[q.idx[q.head++]];
								if (!r.done)
								{
									fromView++;
									BuildTimed(r);
								}
							}
							if (q.head >= q.idx.Count)
							{
								tiles.Remove(key);
							}
						}
					}
				}

				while (cursor < records.Count && Clock.ElapsedTicks - frameStart < budgetTicks)
				{
					Record r = records[cursor++];
					if (!r.done)
					{
						BuildTimed(r);
					}
				}
				DrainCoalesce.Flush();
				allocBuild += GC.GetTotalMemory(false) - m2;
				if (drainGcHeld)
				{
					AsyncLoad.CapCheck("the defer-object drain", AsyncLoad.DrainCapBytes);
				}
				allocLastEnd = GC.GetTotalMemory(false);
				HoldNetworks();
				long frame = Clock.ElapsedTicks - frameStart;
				drainTicks += frame;
				if (frame > worstFrameTicks)
				{
					worstFrameTicks = frame;
				}
				drainFrames++;
				if (cursor >= records.Count)
				{
					// Every record is marked done when it is taken, by the drain, the hook or a
					// flush, so a finished sequential sweep is a finished drain.
					StartPending();
					break;
				}
				LoadNotice.Update(built + buildFailed);
				yield return null;
			}
			double f = Stopwatch.Frequency;
			PerfLog.Info(string.Format(CultureInfo.InvariantCulture,
				"defer-object: built {0:N0} records in {1:0.0} s of wall clock over {2:N0} frames -- {3:0.0} s of main thread ({4:0.0} ms a frame, worst {5:0.0} ms); {6:N0} from the camera's tiles first, {7:N0} by a lookup (GetInstance), {8:N0} by a save, {9:N0} failed to build",
				built, Clock.ElapsedTicks / f - beganAt, drainFrames, drainTicks / f, drainFrames > 0 ? drainTicks * 1000.0 / (drainFrames * f) : 0.0,
				worstFrameTicks * 1000.0 / f, fromView, fromHook, fromFlush, buildFailed));
			if (slicedFrames > 0 || idleFrames > 0)
			{
				double wall = Clock.ElapsedTicks / f - beganAt;
				PerfLog.Info(string.Format(CultureInfo.InvariantCulture,
					"defer-object: frame-rate target {0:0} fps -- {1:0.0} fps over the drain, mean slice {2:0.00} ms, the game's own frame {3:0.0} ms, {4:N0} of {5:N0} frames over their target by 10%; {6:N0} idle frames at {7}",
					FastLoadOptions.AsyncLoadDeferFps, drainFrames / wall, sliceSum / slicedFrames, (wall - drainTicks / f) * 1000.0 / drainFrames, framesOver, slicedFrames, idleFrames,
					FastLoadOptions.AsyncLoadDeferIdleFps > 0 ? FastLoadOptions.AsyncLoadDeferIdleFps.ToString("0", CultureInfo.InvariantCulture) + " fps" : "the full budget"));
			}
			PerfLog.Info(string.Format(CultureInfo.InvariantCulture,
				"defer-object: drain split -- SaveLoadRoot.Load {0:0.0} s, Start() {1:0.0} s, other {2:0.0} s; {3:N0} collections during the drain (collector {4}); slowest record {5} at {6:0.0} ms",
				loadTicks / f, startTicks / f, (drainTicks - loadTicks - startTicks) / f, GC.CollectionCount(0) - drainGcBefore,
				drainGcHeld ? "held" : "live", worstRecordName ?? "-", worstRecordTicks * 1000.0 / f));
			PerfLog.Info(string.Format(CultureInfo.InvariantCulture,
				"defer-object: drain allocation -- builds {0:0.00} GB, Start() {1:0.00} GB, clone pump {2:0.00} GB, the game between drain frames {3:0.00} GB",
				allocBuild / 1e9, allocStart / 1e9, allocPump / 1e9, allocOutside / 1e9));
			AsyncLoad.PhaseTicks(out long inst, out long act, out long deser);
			int spare = DropClones();
			AsyncLoad.LogPrefabCost("drain objects by prefab");
			PerfLog.Info(string.Format(CultureInfo.InvariantCulture,
				"defer-object: drain load split -- instantiate {0:0.0} s, activate {1:0.0} s, deserialize {2:0.0} s; InstantiateAsync {3:N0} clones issued, {4:N0} used, {5:N0} spare destroyed{6}",
				(inst - drainInst0) / f, (act - drainAct0) / f, (deser - drainDeser0) / f, clonesIssued, clonesUsed, spare,
				FastLoadOptions.AsyncLoadNoBatch ? " (batching off)" : ""));
			LogDeserSplit(f);
			Abandon(null);
		}

		/// <summary>Drops every record and the body they point into. With a reason, the records
		/// still unbuilt are lost and that is reported: it only happens when the world they belong
		/// to has gone.</summary>
		/// <summary>The drain's deserialize, split by what could leave the main thread: the mapping
		/// (FastDeser's reads and field stores, less its callbacks) could; the component lookup
		/// (GetComponents, Unity API), the [OnDeserialized] callbacks and ISaveLoadableDetails
		/// (arbitrary Klei code on live game state) could not.</summary>
		private static void LogDeserSplit(double f)
		{
			PerfLog.Info(string.Format(CultureInfo.InvariantCulture,
				"defer-object: drain deserialize split -- lookup {0:0.00} s (GetComponents + layout {1:0.00} s), component managers {2:0.00} s, mapping {3:0.00} s (of it callbacks {4:0.00} s), ISaveLoadableDetails {5:0.00} s; {6:N0} mappings, steps fast {7:N0} / composite {8:N0} / generic {9:N0} / property {10:N0}",
				FastLoadInternal.LookupTicks / f, FastLoadInternal.ComponentsTicks / f, FastLoadInternal.ManagerTicks / f, FastLoadInternal.MappingTicks / f,
				FastDeser.CallbackTicks / f, FastLoadInternal.DetailsTicks / f, FastDeser.Runs, FastDeser.FastSteps, FastDeser.CompositeSteps,
				FastDeser.GenericSteps, FastDeser.PropertySteps));
			var comps = new List<KeyValuePair<string, long[]>>(FastLoadInternal.ByComponent);
			comps.Sort((x, y) => y.Value[1].CompareTo(x.Value[1]));
			PerfLog.Info("defer-object: drain deserialize by component (n, ms, MB): " + string.Join(", ", comps.GetRange(0, Math.Min(25, comps.Count)).ConvertAll(kv =>
				string.Format(CultureInfo.InvariantCulture, "{0} {1:N0} {2:0} {3:0.0}", kv.Key, kv.Value[0], kv.Value[1] * 1000.0 / f, kv.Value[2] / 1e6)).ToArray()));
		}

		private static void Abandon(string why)
		{
			LoadNotice.Hide();
			DrainCoalesce.Flush();
			DrainCoalesce.Report();
			DrainPause.Release();
			DrainQuiet.End();
			ReleaseNetworks();
			if (drainGcHeld)
			{
				drainGcHeld = false;
				UnityEngine.Scripting.GarbageCollector.GCMode = UnityEngine.Scripting.GarbageCollector.Mode.Enabled;
				AsyncLoad.MemLine("the drain's final collection, before");
				long g0 = Clock.ElapsedTicks;
				double cpu0 = AsyncLoad.ProcessCpuSeconds();
				GC.Collect();
				double took = (Clock.ElapsedTicks - g0) / (double)Stopwatch.Frequency, cpu = AsyncLoad.ProcessCpuSeconds() - cpu0;
				PerfLog.Info(string.Format(CultureInfo.InvariantCulture, "defer-object: collector back on after the drain; one full collection in {0:0.00} s ({1:0.00} s process CPU, {2:0.0} cores busy)",
					took, cpu, took > 0 ? cpu / took : 0.0));
				AsyncLoad.MemLine("the drain's final collection, after");
			}
			if (why != null)
			{
				int left = 0;
				foreach (Record r in records)
				{
					if (!r.done)
					{
						left++;
					}
				}
				PerfLog.Warn("defer-object: " + why + " with " + left.ToString("N0", CultureInfo.InvariantCulture) + " records unbuilt; drain abandoned");
			}
			Draining = false;
			DropClones();
			records.Clear();
			byId.Clear();
			tiles.Clear();
			toStart.Clear();
			plan = null;
			ends = null;
			body = null;
			kpidTemplate = null;
			cursor = 0;
			if (host != null)
			{
				UnityEngine.Object.Destroy(host);
				host = null;
			}
		}

		/// <summary>A load that failed: nothing it planned is wanted.</summary>
		public static void LoadFailed()
		{
			if (records.Count > 0 || plan != null)
			{
				Abandon(null);
			}
		}
	}
}
