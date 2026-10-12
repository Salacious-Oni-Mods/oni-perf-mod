// Adapted from the game's own code and NOT covered by the MIT licence: see NOTICE.
using HarmonyLib;
using KSerialization;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.UI;

namespace OniPerf
{
	/// <summary>
	/// the fast loader: the save load, spread over frames so the game never freezes.
	///
	/// Klei's load is one call: SaveLoader.OnSpawn -> Load(string) -> Load(IReader) ->
	/// SaveManager.Load's object loop, then every loaded object's Start/Spawn in the next frame.
	/// For the 239,370-object Euphoria save that is ~6.5 min with no frame drawn and the window
	/// "not responding". This replaces that one call with a coroutine that does the same steps
	/// in the same order, and draws its own loading screen between them:
	///
	///   worker thread   File.ReadAllBytes, then zlib decompress (both pure managed, no Unity API)
	///   main, one go    header, type directory, SaveFileRoot, settings, Sim.LoadWorld,
	///                   PostLoadPrefabs -- Klei's Load(IReader) up to the object loop
	///   main, sliced    SaveManager.Load's object loop, FrameBudgetMs per frame
	///   main, one go    the rest of Load(IReader) and Load(string)
	///   main, sliced    the Start() calls that were held back, in the order Unity made them
	///
	/// Holding the rest of the scene back while the loop runs across frames:
	///   * KMonoBehaviour.Start is gated: while the load runs, a Start queues its component and
	///     returns. Klei's order was [scene Starts after SaveLoader] then [loaded objects'
	///     Starts]; Unity calls them in that order across the frames, and the queue replays it.
	///   * Game.Update / Game.LateUpdate are gated: Game is not spawned until the replay.
	///   * Every camera is switched off, so the half-loaded world is not rendered; a screen-space
	///     canvas of our own is the only thing drawn.
	///
	/// The game code followed below is from its SaveLoader.cs 240-440 and 903-975 and
	/// SaveManager.cs 286-343 (build 744825). The worldgen path (no save to load) is left to
	/// Klei: the prefix only takes over when WorldGen.CanLoad(path).
	/// </summary>
	internal static class AsyncLoad
	{
		private static float FrameBudgetMs => FastLoadOptions.AsyncLoadBudgetMs;
		// The caps follow `loadGcHold`'s rule (LoadHold.cs): half the installed memory, at most
		// 24 GB, and no hold at all below 24 GB installed. On a 64 GB PC that is the 24 GB the loader
		// was measured with.
		private static long NoGcCapBytes => LoadHold.CapBytes;
		// After a cap collection, the next one waits until private bytes grow this much past what
		// the last one left (4 GB at the 24 GB cap). On the Euphoria save, five collections at
		// 24.47..25.10 GB freed almost nothing (the growth is live colony data), costing 6.8 s with
		// the parallel-marking runtime and ~11 s on the stock one.
		private static long NoGcCapStepBytes => LoadHold.StepBytes;
		// The defer-object drain's cap, one step above the loop's (28 GB at the 24 GB cap). With the
		// network rebuilds held (DeferredObject.HoldNetworks) the Euphoria drain makes ~7 GB of
		// garbage instead of ~31 and ends at ~27 GB private with no collection, under the 33 GB the
		// 24 GB cap's three collections used to peak at.
		internal static long DrainCapBytes => LoadHold.CapBytes + LoadHold.StepBytes;
		private static long capNextBytes;

		// InstantiateAsync batching: a run of one prefab is cloned in chunks of BatchChunk, and up
		// to BatchLookahead clones are queued ahead of the object being loaded, so Unity copies the
		// next chunks on its worker threads while this one activates and deserializes.
		private const int BatchChunk = 1024;
		private const int BatchMin = 16;
		private const int BatchLookahead = 8192;
		private static float BatchIntegrationMs => FastLoadOptions.AsyncLoadIntegrationMs;
		private static long waitTicks;
		// The object loop's "other", split: Alloc() calls, Status() text, time off the coroutine
		// between a budget yield and its resume (the rest of Unity's frame), and the cap checks.
		private static long allocTicks, allocCalls, statusTicks, yieldTicks, capTicks;
		private static int loopYields;
		private static int batchedObjects;
		private static Batcher batcher;
		private static int capChecks, capCollections;
		// True while the collector hold has collections off; ReleaseGc ends it (every exit path).
		private static bool gcHeld;

		private static bool gate;
		private static int heldBeforeLoop;
		private static bool fastLoadInternal;
		private static readonly List<KMonoBehaviour> held = new List<KMonoBehaviour>();
		// Every camera's real culling mask while the load draws nothing; RestoreCameras puts them back.
		private static readonly Dictionary<Camera, int> cameraMasks = new Dictionary<Camera, int>();

		/// <summary>True from SaveLoader.OnSpawn until the last held Start() has run (or the load
		/// failed). The meter waits on it: Game.OnSpawn fires inside the replay, so "Game spawned
		/// plus 3 s" is no longer the end of a load.</summary>
		public static bool Busy { get; private set; }

		// The object-loop split (SaveLoadRoot.Load, inlined): ticks and allocated bytes for
		// KInstantiate, SetActive (Awake + OnPrefabInit) and LoadInternal (deserialize, including
		// the objects a Storage loads through the nested Klei call), and the same per prefab.
		private static long instTicks, actTicks, deserTicks, instBytes, actBytes, deserBytes;
		private sealed class Cost
		{
			public int n;
			public long inst, act, deser, actBytes, bytes;
			// Replay table only: the slowest single Start(), to tell a one-off first call from a per-object cost.
			public long max;
			public string maxAt;
		}
		private static readonly Dictionary<string, Cost> prefabCost = new Dictionary<string, Cost>();
		private static readonly Dictionary<Type, Cost> startCost = new Dictionary<Type, Cost>();
		private static readonly System.Action<GameObject, IReader> LoadInternalFn =
			(System.Action<GameObject, IReader>)Delegate.CreateDelegate(typeof(System.Action<GameObject, IReader>),
				AccessTools.Method(typeof(SaveLoadRoot), "LoadInternal"));

		[DllImport("kernel32.dll")]
		private static extern IntPtr GetCurrentProcess();
		[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
		private static extern IntPtr GetModuleHandleW(string name);
		[DllImport("kernel32.dll", CharSet = CharSet.Ansi, BestFitMapping = false)]
		private static extern IntPtr GetProcAddress(IntPtr module, string name);
		[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
		private delegate IntPtr VersionFn();

		/// <summary>Which GC runtime is loaded: oni-gc-runtime builds export oni_gc_version (as read by
		/// OniFramework's GcRuntime); anything else is reported as the stock collector.</summary>
		private static string GcRuntimeName()
		{
			try
			{
				IntPtr m = GetModuleHandleW("mono-2.0-bdwgc.dll");
				IntPtr fn = m == IntPtr.Zero ? IntPtr.Zero : GetProcAddress(m, "oni_gc_version");
				if (fn == IntPtr.Zero) return "stock (no oni_gc_version export)";
				IntPtr v = ((VersionFn)Marshal.GetDelegateForFunctionPointer(fn, typeof(VersionFn)))();
				string markers = Environment.GetEnvironmentVariable("GC_MARKERS");
				return "oni-gc " + (v == IntPtr.Zero ? "?" : Marshal.PtrToStringAnsi(v)) + (markers != null ? ", GC_MARKERS=" + markers : "");
			}
			catch (Exception e)
			{
				return "unknown (" + e.Message + ")";
			}
		}
		[DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo")]
		private static extern bool GetProcessMemoryInfo(IntPtr process, out ProcessMemoryCountersEx counters, int size);
		[StructLayout(LayoutKind.Sequential)]
		private struct ProcessMemoryCountersEx
		{
			public int cb;
			public int PageFaultCount;
			public IntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
				QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage;
		}

		/// <summary>Private commit, and its peak, in bytes. Process.PrivateMemorySize64 reads 0
		/// under this Mono (measured on the Euphoria save).</summary>
		private static long PrivateBytes(out long peak)
		{
			var m = new ProcessMemoryCountersEx { cb = Marshal.SizeOf(typeof(ProcessMemoryCountersEx)) };
			if (!GetProcessMemoryInfo(GetCurrentProcess(), out m, m.cb))
			{
				peak = 0;
				return 0;
			}
			peak = (long)m.PeakPagefileUsage;
			return (long)m.PrivateUsage;
		}

		/// <summary>Managed USED vs managed RESERVED, plus native, at one point in the load.
		///
		/// This was written to check a suspicion: that GC.GetTotalMemory under this Boehm
		/// Mono reports the heap SIZE, which never shrinks, so "managed still 12 GB after a full
		/// collection" would read the same whether the heap were live or mostly free. The first
		/// run refuted it: GetTotalMemory printed used, not reserved, at all
		/// three points, and across the post-load collection it FELL 11.23 -> 10.85 GB while
		/// reserved ROSE 11.23 -> 11.96 GB. A heap-size metric cannot fall.
		///
		/// Kept anyway, because reserved is the number GetTotalMemory does not give, and used vs
		/// reserved is the only in-process read on fragmentation. Both are block-granular under
		/// Boehm: a block holding one live object counts as wholly used, so "used" is an upper
		/// bound on live bytes, not live bytes.</summary>
		internal static void MemLine(string at)
		{
			try
			{
				long used = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
				long heap = UnityEngine.Profiling.Profiler.GetMonoHeapSizeLong();
				long natAlloc = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong();
				long natRes = UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong();
				long priv = PrivateBytes(out long peak);
				PerfLog.Info(string.Format(
					"asyncload: memory at {0}: managed used {1:0.00} GB of {2:0.00} GB reserved ({3:0.0}% live-or-uncollected); native allocated {4:0.00} GB of {5:0.00} GB reserved; private {6:0.00} GB (peak {7:0.00} GB); GC.GetTotalMemory {8:0.00} GB",
					at, used / 1e9, heap / 1e9, heap > 0 ? used * 100.0 / heap : 0.0,
					natAlloc / 1e9, natRes / 1e9, priv / 1e9, peak / 1e9, GC.GetTotalMemory(false) / 1e9));
			}
			catch (Exception e)
			{
				PerfLog.Warn("asyncload: memory at " + at + " unavailable: " + e.Message);
			}
		}

		// GC.GetAllocatedBytesForCurrentThread reads 0 under this Boehm Mono. The heap size is a
		// byte count of allocation only while collections are off (the collector hold).
		// Timed, because it runs four times per object and three of those land inside the
		// instantiate/activate/deserialize stamps.
		private static long Alloc()
		{
			long s0 = Clock.ElapsedTicks;
			long r = GC.GetTotalMemory(false);
			allocTicks += Clock.ElapsedTicks - s0;
			allocCalls++;
			return r;
		}

		/// <summary>SetupCoroutine.InvokeMoveNext: while a load is gated, a coroutine that is not
		/// ours or Unity's own yield instruction stays where it is. A vanilla load holds the main
		/// thread for its whole length, so no coroutine steps then; sliced, Timelapser.Render stepped
		/// into an unstarted OverlayScreen (seen on the Euphoria save). Unity reads Current again, so the
		/// coroutine re-waits on what it last yielded.</summary>
		private static bool MoveNextPrefix(IEnumerator enumerator, IntPtr returnValueAddress)
		{
			if (!gate || enumerator == null || returnValueAddress == IntPtr.Zero)
			{
				return true;
			}
			Assembly a = enumerator.GetType().Assembly;
			if (a == OurAssembly || a == UnityAssembly)
			{
				return true;
			}
			pausedSteps++;
			Marshal.WriteByte(returnValueAddress, 1);
			return false;
		}
		private static readonly Assembly OurAssembly = typeof(AsyncLoad).Assembly;
		private static readonly Assembly UnityAssembly = typeof(MonoBehaviour).Assembly;
		private static long pausedSteps;

		private static readonly Stopwatch Clock = Stopwatch.StartNew();
		private static long startTicks;
		private static string status = "";
		private static string detail = "";
		private static int objectsDone, objectsTotal;
		private static Text text;
		private static GameObject host;

		// Klei privates, resolved once.
		private static readonly MethodInfo GameInfoSetter = AccessTools.PropertySetter(typeof(SaveLoader), nameof(SaveLoader.GameInfo));
		private static readonly MethodInfo LoadedFromSaveSetter = AccessTools.PropertySetter(typeof(SaveLoader), nameof(SaveLoader.loadedFromSave));
		private static readonly FieldInfo MustRestartOnFail = AccessTools.Field(typeof(SaveLoader), "mustRestartOnFail");
		private static readonly FieldInfo SaveFileCorrupt = AccessTools.Field(typeof(SaveLoader), "saveFileCorrupt");
		private static readonly FieldInfo LastUncompressedSize = AccessTools.Field(typeof(SaveLoader), "lastUncompressedSize");
		private static readonly MethodInfo LogActiveMods = AccessTools.Method(typeof(SaveLoader), "LogActiveMods");
		private static readonly MethodInfo BytesToFloat = AccessTools.Method(typeof(SaveLoader), "BytesToFloat");
		private static readonly MethodInfo DecompressContents = AccessTools.Method(typeof(SaveLoader), "DecompressContents");
		private static readonly FieldInfo PrefabMap = AccessTools.Field(typeof(SaveManager), "prefabMap");
		private static readonly FieldInfo SceneObjects = AccessTools.Field(typeof(SaveManager), "sceneObjects");
		private static readonly MethodInfo ClearScene = AccessTools.Method(typeof(SaveManager), "ClearScene");
		// Klei.SaveFileRoot is internal; its fields are read by name.
		private static readonly Type SaveFileRootType = AccessTools.TypeByName("Klei.SaveFileRoot");

		private static T RootField<T>(object root, string name) => (T)AccessTools.Field(SaveFileRootType, name).GetValue(root);

		private static void SetRootField(object root, string name, object value) => AccessTools.Field(SaveFileRootType, name).SetValue(root, value);

		public static void Install(Harmony harmony)
		{
			foreach (var m in new MemberInfo[] { GameInfoSetter, LoadedFromSaveSetter, MustRestartOnFail, SaveFileCorrupt, LastUncompressedSize,
				LogActiveMods, BytesToFloat, DecompressContents, PrefabMap, SceneObjects, ClearScene, SaveFileRootType })
			{
				if (m == null)
				{
					PerfLog.Warn("asyncload: a Klei member moved (build changed?); async load NOT installed");
					return;
				}
			}
			harmony.Patch(AccessTools.Method(typeof(SaveLoader), "OnSpawn"), prefix: new HarmonyMethod(typeof(AsyncLoad), nameof(OnSpawnPrefix)));
			harmony.Patch(AccessTools.Method(typeof(KMonoBehaviour), nameof(KMonoBehaviour.Start)), prefix: new HarmonyMethod(typeof(AsyncLoad), nameof(StartPrefix)));
			// The frontend's EventSystem keeps ticking while the load runs across frames, and its
			// VirtualInputModule asks SteamInputInterpreter for cursor movement every frame.
			// GetSteamCursorMovement walks m_nInputs controllers of a controllerDatas array that
			// only GameInputManager.Update sizes (the game's SteamInputInterpreter) -- and that Update is gated with the
			// rest of the loading scene, so with a controller connected every frame throws
			// IndexOutOfRange: 2,584 of them per Euphoria load, against 0 on Klei's loader, which
			// draws no frame at all. Nothing needs to click during the load, so the EventSystem is
			// gated the same way Game.Update is.
			Type eventSystem = AccessTools.TypeByName("UnityEngine.EventSystems.EventSystem");
			MethodInfo eventSystemUpdate = (eventSystem != null) ? AccessTools.Method(eventSystem, "Update") : null;
			if (eventSystemUpdate != null)
			{
				harmony.Patch(eventSystemUpdate, prefix: new HarmonyMethod(typeof(AsyncLoad), nameof(EventSystemUpdatePrefix)));
			}
			else
			{
				PerfLog.Warn("asyncload: no EventSystems.EventSystem.Update; input keeps ticking during a load");
			}
			harmony.Patch(AccessTools.Method(typeof(CameraController), "OnPrefabInit"),
				prefix: new HarmonyMethod(typeof(AsyncLoad), nameof(CameraInitPrefix)),
				postfix: new HarmonyMethod(typeof(AsyncLoad), nameof(CameraInitPostfix)));
			// A save must never run with deferred objects still unregistered (DeferredSpawn.FlushForSave,
			// DeferredObject.FlushForSave).
			// Save(string, bool, bool), not Save(BinaryWriter): the string overload calls
			// KSerialization.Manager.Clear() (the game's SaveLoader.cs 775) before it reaches the writer,
			// and that drops the save's deserialization templates every deferred record is read
			// with. Flushing from the inner overload read each record against the current type
			// layouts, ~1 KB short per record (seen on the first live save during a drain).
			MethodInfo saveBody = AccessTools.Method(typeof(SaveLoader), "Save", new[] { typeof(string), typeof(bool), typeof(bool) });
			if (saveBody != null)
			{
				harmony.Patch(saveBody, prefix: new HarmonyMethod(typeof(AsyncLoad), nameof(SaveFlushPrefix)));
			}
			else if (FastLoadOptions.AsyncLoadDeferScreens >= 0f || FastLoadOptions.AsyncLoadDeferObjectScreens >= 0f)
			{
				PerfLog.Warn("asyncload: no SaveLoader.Save(string, bool, bool); deferral switched OFF, because a save during the drain would drop every deferred object");
				FastLoadOptions.AsyncLoadDeferScreens = -1f;
				FastLoadOptions.AsyncLoadDeferObjectScreens = -1f;
			}
			// Here, at mod load, and not at the first load: see DeferredObject.Install.
			DeferredObject.Install(harmony);
			MethodInfo moveNext = AccessTools.Method(AccessTools.TypeByName("UnityEngine.SetupCoroutine"), "InvokeMoveNext");
			if (moveNext != null)
			{
				harmony.Patch(moveNext, prefix: new HarmonyMethod(typeof(AsyncLoad), nameof(MoveNextPrefix)));
			}
			else
			{
				PerfLog.Warn("asyncload: no UnityEngine.SetupCoroutine.InvokeMoveNext; coroutines keep stepping during a load");
			}
			// The state-machine string and type-name caches are OniPerf's own `smRestore` and
			// `typeNameCache` levers, installed by OniPerfMod when they are on.
			FastDeser.Install(harmony);
			if (FastLoadInternal.Ready)
			{
				FastLoadInternal.Install(harmony);
			}
			int gated = GateUpdates(harmony);
			PerfLog.Info("fastload: installed; frame budget " + FrameBudgetMs + " ms, integration " + BatchIntegrationMs + " ms; " + gated + " per-frame Unity messages gated during a load; collector "
				+ (FastLoadOptions.AsyncLoadNoGc ? string.Format("held during a load, cap {0:0.0} GB private, drain cap {1:0.0} GB", NoGcCapBytes / 1e9, DrainCapBytes / 1e9) : "NOT held (see the loadhold line)"));
		}

		/// <summary>
		/// While the load runs over frames, Unity keeps calling Update/LateUpdate/FixedUpdate on
		/// every scene component, and Klei's are written for a world that loaded in one call:
		/// WaterCubes.LateUpdate, for one, NREs every frame until Game.OnSpawn has run. So every
		/// such method on a MonoBehaviour in the game's two assemblies returns early while the
		/// gate is up -- but only for components in the scene being loaded. DontDestroyOnLoad
		/// objects (Global, audio, input) keep running, and so does our own loading screen.
		/// Outside a load the prefix is one static bool read.
		/// </summary>
		private static int GateUpdates(Harmony harmony)
		{
			var prefix = new HarmonyMethod(typeof(AsyncLoad), nameof(UpdatePrefix));
			int n = 0;
			foreach (Assembly asm in new[] { typeof(Game).Assembly, typeof(KMonoBehaviour).Assembly })
			{
				Type[] types;
				try
				{
					types = asm.GetTypes();
				}
				catch (ReflectionTypeLoadException e)
				{
					types = e.Types.Where(t => t != null).ToArray();
				}
				foreach (Type t in types)
				{
					if (!typeof(MonoBehaviour).IsAssignableFrom(t) || t.IsGenericTypeDefinition)
					{
						continue;
					}
					foreach (MethodInfo m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
					{
						if (Array.IndexOf(GatedMessages, m.Name) < 0 || m.IsAbstract || m.ContainsGenericParameters || m.ReturnType != typeof(void))
						{
							continue;
						}
						string name = m.Name;
						try
						{
							harmony.Patch(m, prefix: prefix);
							n++;
						}
						catch (Exception e)
						{
							PerfLog.Warn("asyncload: could not gate " + t.FullName + "." + name + ": " + e.Message);
						}
					}
				}
			}
			return n;
		}

		// Unity messages that run every frame, including the camera callbacks: with the cameras
		// still enabled, LightBufferCompositor.OnRenderImage and friends run too.
		private static readonly string[] GatedMessages =
		{
			"Update", "LateUpdate", "FixedUpdate", "OnRenderImage", "OnPreRender", "OnPostRender", "OnPreCull", "OnRenderObject", "OnGUI",
		};

		private static UnityEngine.SceneManagement.Scene loadingScene;

		private static bool UpdatePrefix(MonoBehaviour __instance)
		{
			return !gate || __instance == null || __instance.gameObject.scene != loadingScene;
		}

		/// <summary>No UI input while the load runs; see the patch site for what throws otherwise.</summary>
		private static bool EventSystemUpdatePrefix()
		{
			return !Busy;
		}

		private static bool StartPrefix(KMonoBehaviour __instance)
		{
			// A component created by a replayed Start() (Game.OnSpawn builds the HUD screens) is
			// held too and goes to the end of the list: Unity starts it after every pending world
			// object, and ClusterMapScreen / ColonyDiagnosticScreen depend on that order (starting
			// them at once broke both, on the test save).
			// Only the scene being loaded is held, as with the Update gate. Holding the frontend's
			// components too left MinionSelectScreen's CharacterContainers unspawned when the
			// frontend unloaded, and their OnForcedCleanUp NREs on a list only OnSpawn creates.
			if (!gate || App.IsExiting || __instance.gameObject.scene != loadingScene)
			{
				return true;
			}
			held.Add(__instance);
			return false;
		}

		/// <summary>Klei's SaveLoader.OnSpawn, up to the Load(path) call; the rest runs in Run.</summary>
		private static bool OnSpawnPrefix(SaveLoader __instance)
		{
			string path = SaveLoader.GetActiveSaveFilePath();
			if (!ProcGenGame.WorldGen.CanLoad(path))
			{
				return true;
			}
			Sim.SIM_Initialize(Sim.DLL_MessageHandler);
			SimMessages.CreateSimElementsTable(ElementLoader.elements);
			SimMessages.CreateDiseaseTable(Db.Get().Diseases);
			LoadedFromSaveSetter.Invoke(__instance, new object[] { true });
			loadingScene = __instance.gameObject.scene;
			gate = true;
			Busy = true;
			held.Clear();
			capNextBytes = 0;
			instTicks = actTicks = deserTicks = instBytes = actBytes = deserBytes = 0;
			FastDeser.ResetCounters();
			SmRestore.ResetCounters();
			prefabCost.Clear();
			startCost.Clear();
			PerfLog.Info("asyncload: GC runtime " + GcRuntimeName());
			host = new GameObject("OniPerf.AsyncLoad");
			UnityEngine.Object.DontDestroyOnLoad(host);
			host.AddComponent<AsyncLoadHost>().StartCoroutine(Run(__instance, path));
			return false;
		}

		private sealed class Ctx
		{
			public SaveLoader loader;
			public string path;
			public byte[] file;
			public byte[] body;
			public Exception workerError;
			public IReader reader;
			public Deserializer deserializer;
			public object root;
			public bool ok = true;
		}

		private static IEnumerator Run(SaveLoader loader, string path)
		{
			startTicks = Clock.ElapsedTicks;
			// Cameras stay enabled -- Camera.main is null for a disabled camera, and
			// CameraController.OnPrefabInit and everything after it assume it is not -- but draw
			// nothing until the world is loaded.
			cameraMasks.Clear();
			foreach (Camera cam in Camera.allCameras)
			{
				cameraMasks[cam] = cam.cullingMask;
				cam.cullingMask = 0;
			}
			BuildScreen();
			var c = new Ctx { loader = loader, path = path };

			// 1. read the file off the main thread
			Status("reading " + Path.GetFileName(path));
			var t = new Thread(() =>
			{
				try
				{
					c.file = File.ReadAllBytes(path);
				}
				catch (Exception e)
				{
					c.workerError = e;
				}
			}) { IsBackground = true, Name = "asyncload read" };
			t.Start();
			while (t.IsAlive)
			{
				yield return null;
			}

			// 2. header + type directory (main), decompress (worker)
			SaveGame.Header header = default;
			if (c.workerError == null)
			{
				Step(c, "header", () => header = ReadHeader(c));
			}
			else
			{
				Fail(c, c.workerError);
			}
			if (c.ok && header.IsCompressed)
			{
				Status("decompressing");
				int offset = c.reader.Position;
				t = new Thread(() =>
				{
					try
					{
						int n = c.file.Length - offset;
						byte[] compressed = new byte[n];
						Array.Copy(c.file, offset, compressed, 0, n);
						c.body = (byte[])DecompressContents.Invoke(null, new object[] { compressed });
					}
					catch (Exception e)
					{
						c.workerError = e is TargetInvocationException tie ? tie.InnerException : e;
					}
				}) { IsBackground = true, Name = "asyncload inflate" };
				t.Start();
				while (t.IsAlive)
				{
					yield return null;
				}
				if (c.workerError != null)
				{
					Fail(c, c.workerError);
				}
				else
				{
					LastUncompressedSize.SetValue(loader, c.body.Length);
					c.reader = new FastReader(c.body);
				}
			}
			else if (c.ok)
			{
				LastUncompressedSize.SetValue(loader, c.file.Length);
			}
			c.file = null;
			yield return null;

			// 3. Load(IReader) up to the object loop
			if (c.ok)
			{
				Status("world settings and sim");
				yield return null;
				Step(c, "world", () => LoadWorldPrelude(c));
			}

			// 4. the object loop, sliced. One frame first: SceneInitializer.PostLoadPrefabs (end of
			// the prelude) has just made Game and the screens, and Unity calls their Start() on the
			// next frame. Held then, they count as scene components and replay in one batch.
			if (c.ok)
			{
				yield return null;
			}
			if (c.ok)
			{
				IEnumerator objects = LoadObjects(c);
				while (true)
				{
					bool more;
					try
					{
						more = objects.MoveNext();
					}
					catch (Exception e)
					{
						Fail(c, e);
						break;
					}
					if (!more)
					{
						break;
					}
					yield return null;
				}
			}

			// 5. the rest of Load(IReader) and Load(string)
			if (c.ok)
			{
				Status("game state");
				yield return null;
				Step(c, "epilogue", () => LoadEpilogue(c));
			}

			if (!c.ok)
			{
				// Klei's OnSpawn failure path, with mustRestartOnFail as Load(IReader) left it.
				ReleaseGc("failed load");
				DeferredObject.LoadFailed();
				gate = false;
				Busy = false;
				batcher?.Abort();
				batcher = null;
				held.Clear();
				SaveFileCorrupt.SetValue(loader, true);
				LoadedFromSaveSetter.Invoke(loader, new object[] { false });
				SaveLoader.SetActiveSaveFilePath(null);
				Sim.Shutdown();
				RestoreCameras();
				UnityEngine.Object.Destroy(host);
				App.LoadScene("frontend");
				yield break;
			}

			// 6. replay the held Start() calls in Unity's order, sliced
			int total = held.Count, replayed = 0, replayYields = 0;
			long replayOffTicks = 0L;
			long frameStart = Clock.ElapsedTicks;
			double replayStart = Elapsed();
			long replayBytes = Alloc();
			int replayGcBefore = GC.CollectionCount(0);
			// The Start() deferral: with it armed, a world object far enough from the camera the
			// save restores does not start here at all -- it is handed to DeferredSpawn and started
			// after the loading screen. Scene components (i < heldBeforeLoop) are never deferred.
			bool deferring = DeferredSpawn.Armed;
			int deferredCount = 0;
			PerfLog.Info("asyncload: world loaded in " + Elapsed().ToString("0.0") + " s; replaying " + total + " held Start() calls (" + heldBeforeLoop + " scene components first, unsliced)");
			for (int i = 0; i < held.Count; i++)
			{
				KMonoBehaviour k = held[i];
				if (k != null && !k.isSpawned)
				{
					bool world = i >= heldBeforeLoop;
					// Deferrable means "built by the object loop", which is a narrower thing than
					// "not a scene component". A Start() replayed here can ADD to `held` -- Game.OnSpawn
					// builds the HUD screens -- and those land past `total`. They are UI, their
					// transform is a canvas position that means nothing on the map, and deferring one
					// breaks the frame: OverlayScreen held back this way left StatusItemRenderer
					// NREing out of Game.LateUpdate 817 times before the drain reached it
					// (on the test save, deferring small objects).
					bool deferrable = world && i < total;
					Vector3 censusPos = default(Vector3);
					bool censusHasPos = false;
					if (world && deferring)
					{
						Transform tr = k.transform;
						if (tr != null)
						{
							censusPos = tr.position;
							censusHasPos = true;
						}
					}
					// GetType() is read before the timer ONLY on the deferring arm, because the
					// keep-list needs it to decide. On every other arm it stays inside the timed
					// region below, where the replay's published numbers have always counted it.
					Type type = null;
					bool deferredHere = false;
					if (deferring && deferrable && censusHasPos)
					{
						type = k.GetType();
						if (DeferredSpawn.ShouldDefer(type, censusPos))
						{
							DeferredSpawn.Hold(k, censusPos);
							deferredHere = true;
							deferredCount++;
						}
					}
					if (!deferredHere)
					{
						long s0 = Clock.ElapsedTicks;
						gate = false;
						k.Start();
						gate = true;
						if (type == null)
						{
							type = k.GetType();
						}
						if (!startCost.TryGetValue(type, out Cost cost))
						{
							startCost[type] = cost = new Cost();
						}
						long dt = Clock.ElapsedTicks - s0;
						cost.n++;
						cost.inst += dt;
						if (dt > cost.max)
						{
							cost.max = dt;
							cost.maxAt = (k != null ? k.name : "destroyed") + " #" + i;
						}
					}
				}
				replayed++;
				// The scene's own components (held before the object loop: Game, the screens, the
				// managers) start in one unbroken batch, as they do in a vanilla load. A yield
				// among them lets a Destroy one of them asks for land first: MinionSelectScreen's
				// CharacterContainers were destroyed unspawned, and NRE'd (on the test save).
				if (i >= heldBeforeLoop && MsSince(frameStart) > FrameBudgetMs)
				{
					if (gcHeld && ++capChecks % 25 == 0)
					{
						CapCheck("Start() " + replayed.ToString("N0"));
					}
					Status("starting objects", string.Format("{0:N0} / {1:N0} components spawned", replayed, held.Count));
					// The replay's headline is wall clock, so it also contains every frame the replay
					// spans -- and heavier Start() bodies buy more frames, not just more work. Without
					// these two counters "payload" and "the frames the payload forced" are one number,
					// which is what the no-op arm could not separate.
					long yieldAt = Clock.ElapsedTicks;
					yield return null;
					frameStart = Clock.ElapsedTicks;
					replayYields++;
					replayOffTicks += frameStart - yieldAt;
				}
			}
			held.Clear();
			gate = false;
			int replayGcs = GC.CollectionCount(0) - replayGcBefore;
			double replayEnd = Elapsed();
			long replayAlloc = Alloc() - replayBytes;
			// Before the release, not after: ReleaseGc runs a full collection and MemLine reads the
			// heap straight afterwards, so a cap applied here is reclaimed by the collection that
			// already happens and shows up in the one reading that means anything (§7.9).
			ReleaseGc("Start() replay");
			RestoreCameras();
			PrivateBytes(out long peakPriv);
			PerfLog.Info(string.Format("asyncload: done in {0:0.0} s ({1:N0} Start() calls replayed in {2:0.0} s, {3:0.00} GB allocated, {4} collections during it; final collection {5:0.0} s); private peak {6:0.00} GB",
				Elapsed(), replayed, replayEnd - replayStart, replayAlloc / 1e9, replayGcs, Elapsed() - replayEnd, peakPriv / 1e9));
			string smLine = SmRestore.Report();
			if (smLine != null)
			{
				PerfLog.Info(smLine);
			}
			double replayOff = replayOffTicks / (double)Stopwatch.Frequency;
			PerfLog.Info(string.Format("asyncload: replay frames: {0:N0} yields, {1:0.0} s off-coroutine ({2:0.0} ms each), {3:0.0} s on the main thread; the off-coroutine share is what the slicing costs, not what Start() costs",
				replayYields, replayOff, replayYields > 0 ? replayOff * 1000.0 / replayYields : 0.0, (replayEnd - replayStart) - replayOff));
			if (deferring)
			{
				PerfLog.Info(string.Format("asyncload: {0:N0} of the {1:N0} held Start() calls were deferred past the loading screen; the {2:N0} that ran are what the replay timing above measures",
					deferredCount, total, replayed - deferredCount));
			}
			LogTop("Start() by type", startCost, t => t.FullName, 15);
			PerfLog.Info("asyncload: " + pausedSteps + " coroutine steps held back during the load");
			pausedSteps = 0;
			Busy = false;
			UnityEngine.Object.Destroy(host);
			// After Busy, deliberately: the loading screen is gone and the game is taking input,
			// which is the whole point of moving this work here.
			DeferredSpawn.Begin();
			DeferredObject.Begin();
		}

		/// <summary>Prefix on SaveLoader.Save(string, bool, bool), ahead of its Manager.Clear(). Whole objects first: building one runs
		/// nothing the Start() deferral holds, and starting it registers it with SaveManager directly.</summary>
		private static void SaveFlushPrefix()
		{
			DeferredObject.FlushForSave();
			DeferredSpawn.FlushForSave();
		}

		private static void Step(Ctx c, string what, System.Action a)
		{
			try
			{
				a();
			}
			catch (Exception e)
			{
				PerfLog.Warn("asyncload: " + what + " failed");
				Fail(c, e);
			}
		}

		private static void Fail(Ctx c, Exception e)
		{
			c.ok = false;
			DebugUtil.LogWarningArgs("\n--- Error loading save ---\n" + e.Message + "\n" + e.StackTrace);
		}

		/// <summary>SaveLoader.Load(string), 903-930: header and type directory.</summary>
		private static SaveGame.Header ReadHeader(Ctx c)
		{
			SaveLoader.SetActiveSaveFilePath(c.path);
			Manager.Clear();
			IReader reader = new FastReader(c.file);
			SaveGame.GameInfo info = SaveGame.GetHeader(reader, out SaveGame.Header header, c.path);
			GameInfoSetter.Invoke(c.loader, new object[] { info });
			ThreadedHttps<KleiMetrics>.Instance.SetExpansionsActive(info.dlcIds);
			DebugUtil.LogArgs(string.Format("Loading save file: {4}\n headerVersion:{0}, buildVersion:{1}, headerSize:{2}, IsCompressed:{3}", header.headerVersion, header.buildVersion, header.headerSize, header.IsCompressed, c.path));
			DebugUtil.LogArgs(string.Format("GameInfo loaded from save header:\n  numberOfCycles:{0},\n  numberOfDuplicants:{1},\n  baseName:{2},\n  isAutoSave:{3},\n  originalSaveName:{4},\n  clusterId:{5},\n  worldTraits:{6},\n  colonyGuid:{7},\n  saveVersion:{8}.{9}", info.numberOfCycles, info.numberOfDuplicants, info.baseName, info.isAutoSave, info.originalSaveName, info.clusterId, (info.worldTraits != null && info.worldTraits.Length != 0) ? string.Join(", ", info.worldTraits) : "<i>none</i>", info.colonyGuid, info.saveMajorVersion, info.saveMinorVersion));
			string originalSaveName = info.originalSaveName;
			if (originalSaveName.Contains("/") || originalSaveName.Contains("\\"))
			{
				info.originalSaveName = SaveLoader.GetOriginalSaveFileName(originalSaveName);
				GameInfoSetter.Invoke(c.loader, new object[] { info });
				UnityEngine.Debug.Log("Migration / Save originalSaveName updated from: `" + originalSaveName + "` => `" + info.originalSaveName + "`");
			}
			if (info.saveMajorVersion == 7 && info.saveMinorVersion < 4)
			{
				Helper.SetTypeInfoMask((SerializationTypeInfo)191);
			}
			Manager.DeserializeDirectory(reader);
			c.reader = reader;
			return header;
		}

		/// <summary>SaveLoader.Load(IReader), 346-420: everything before saveManager.Load.</summary>
		private static void LoadWorldPrelude(Ctx c)
		{
			SaveLoader loader = c.loader;
			IReader reader = c.reader;
			// Klei's line is Debug.Assert(reader.ReadKleiString() == "world"). Debug.Assert is
			// [Conditional("UNITY_ASSERTIONS")], which this assembly does not define, so copying it
			// verbatim compiles the read away and leaves the reader one string behind.
			string worldTag = reader.ReadKleiString();
			if (worldTag != "world")
			{
				throw new InvalidDataException("expected \"world\", read \"" + worldTag + "\"");
			}
			Deserializer deserializer = new Deserializer(reader);
			object saveFileRoot = Activator.CreateInstance(SaveFileRootType, true);
			deserializer.Deserialize(saveFileRoot);
			SaveGame.GameInfo info = loader.GameInfo;
			var requiredMods = RootField<List<ModInfo>>(saveFileRoot, "requiredMods");
			if ((info.saveMajorVersion == 7 || info.saveMinorVersion < 8) && requiredMods != null)
			{
				var converted = new List<KMod.Label>();
				foreach (ModInfo requiredMod in requiredMods)
				{
					converted.Add(new KMod.Label
					{
						id = requiredMod.assetID,
						version = (long)requiredMod.lastModifiedTime,
						distribution_platform = KMod.Label.DistributionPlatform.Steam,
						title = requiredMod.description
					});
				}
				SetRootField(saveFileRoot, "active_mods", converted);
				requiredMods.Clear();
			}
			var activeMods = RootField<List<KMod.Label>>(saveFileRoot, "active_mods");
			KMod.Manager modManager = Global.Instance.modManager;
			modManager.Load(KMod.Content.LayerableFiles);
			if (!modManager.MatchFootprint(activeMods, KMod.Content.LayerableFiles | KMod.Content.Strings | KMod.Content.DLL | KMod.Content.Translation | KMod.Content.Animation))
			{
				DebugUtil.LogWarningArgs("Mod footprint of save file doesn't match current mod configuration");
			}
			string text = $"Mod Footprint ({activeMods.Count}):";
			foreach (KMod.Label active_mod in activeMods)
			{
				text = text + "\n  - " + active_mod.title;
			}
			UnityEngine.Debug.Log(text);
			LogActiveMods.Invoke(loader, null);
			Global.Instance.modManager.SendMetricsEvent();
			ProcGenGame.WorldGen.LoadSettings();
			CustomGameSettings.Instance.LoadClusters();
			if (info.clusterId == null)
			{
				string rootClusterId = RootField<string>(saveFileRoot, "clusterID");
				if (!string.IsNullOrEmpty(rootClusterId))
				{
					info.clusterId = rootClusterId;
				}
				else
				{
					try
					{
						info.clusterId = CustomGameSettings.Instance.GetCurrentQualitySetting(Klei.CustomSettings.CustomGameSettingConfigs.ClusterLayout).id;
					}
					catch
					{
						info.clusterId = ProcGen.WorldGenSettings.ClusterDefaultName;
						CustomGameSettings.Instance.SetQualitySetting(Klei.CustomSettings.CustomGameSettingConfigs.ClusterLayout, info.clusterId);
					}
				}
				GameInfoSetter.Invoke(loader, new object[] { info });
			}
			Game.clusterId = loader.GameInfo.clusterId;
			Game.LoadSettings(deserializer);
			int width = RootField<int>(saveFileRoot, "WidthInCells"), height = RootField<int>(saveFileRoot, "HeightInCells");
			var streamed = RootField<Dictionary<string, byte[]>>(saveFileRoot, "streamed");
			if (FastLoadOptions.AsyncLoadDeferScreens >= 0f || FastLoadOptions.AsyncLoadDeferObjectScreens >= 0f)
			{
				LoadRect.Reset();
				LoadRect.Arm(streamed);
			}
			DeferredSpawn.Arm();
			DeferredObject.Arm();
			GridSettings.Reset(width, height);
			BackwallManager.Clear();
			if (Application.isPlaying)
			{
				Singleton<KBatchedAnimUpdater>.Instance.InitializeGrid();
			}
			Sim.SIM_Initialize(Sim.DLL_MessageHandler);
			SimMessages.CreateSimElementsTable(ElementLoader.elements);
			Sim.AllocateCells(width, height);
			SimMessages.CreateDiseaseTable(Db.Get().Diseases);
			Sim.HandleMessage(SimMessageHashes.ClearUnoccupiedCells, 0, null);
			IReader simReader = !streamed.ContainsKey("Sim") ? reader : new FastReader(streamed["Sim"]);
			if (Sim.LoadWorld(simReader) != 0)
			{
				DebugUtil.LogWarningArgs("\n--- Error loading save ---\nSimDLL found bad data\n");
				Sim.Shutdown();
				c.ok = false;
				return;
			}
			Sim.Start();
			SceneInitializer.Instance.PostLoadPrefabs();
			MustRestartOnFail.SetValue(loader, true);
			c.deserializer = deserializer;
			c.root = saveFileRoot;
		}

		/// <summary>SaveManager.Load(IReader), 286-343, yielding whenever the frame budget is spent.</summary>
		private static IEnumerator LoadObjects(Ctx c)
		{
			SaveManager sm = c.loader.saveManager;
			IReader reader = c.reader;
			char[] magic = reader.ReadChars(4);
			if (magic == null || new string(magic) != "KSAV")
			{
				c.ok = false;
				yield break;
			}
			int major = reader.ReadInt32();
			int minor = reader.ReadInt32();
			if (major != 7 || minor > 38)
			{
				DebugUtil.LogWarningArgs($"SAVE FILE VERSION MISMATCH! Expected {7}.{38} but got {major}.{minor}");
				c.ok = false;
				yield break;
			}
			ClearScene.Invoke(sm, null);
			var prefabMap = (Dictionary<Tag, GameObject>)PrefabMap.GetValue(sm);
			var sceneObjects = (Dictionary<Tag, List<SaveLoadRoot>>)SceneObjects.GetValue(sm);
			heldBeforeLoop = held.Count;
			fastLoadInternal = FastLoadInternal.Ready;
			if (fastLoadInternal) FastLoadInternal.Prepare();
			List<KeyValuePair<string, int>> plan = PlanGroups(reader);
			// The whole-object deferral: decide every record before the batcher asks for clones, so it asks only
			// for the ones the loop will build. keptPlan is plan with deferred records taken out.
			List<KeyValuePair<string, int>> keptPlan = plan;
			bool deferObjects = SaveManager.DEBUG_OnlyLoadThisCellsObjects == -1 && plan.Count > 0
				&& DeferredObject.PreScan(reader.RawBytes(), reader.Position, prefabMap, plan, out keptPlan);
			int recordIndex = 0;
			objectsTotal = 0;
			foreach (var g in plan)
			{
				objectsTotal += g.Value;
			}
			waitTicks = 0;
			allocTicks = allocCalls = statusTicks = yieldTicks = capTicks = 0;
			loopYields = 0;
			batchedObjects = 0;
			float integrationWas = AsyncInstantiateOperation.GetIntegrationTimeMS();
			batcher = null;
			if (!FastLoadOptions.AsyncLoadNoBatch && SaveManager.DEBUG_OnlyLoadThisCellsObjects == -1)
			{
				batcher = new Batcher(keptPlan, prefabMap);
				AsyncInstantiateOperation.SetIntegrationTimeMS(BatchIntegrationMs);
				batcher.Pump();
				PerfLog.Info(string.Format("asyncload: InstantiateAsync batching {0:N0} of {1:N0} objects in {2} chunks of up to {3}",
					batcher.Objects, objectsTotal, batcher.Chunks, BatchChunk));
			}
			objectsDone = 0;
			// the collector hold: no collections during the object loop, one at its end.
			// A profile of the Euphoria save ran 193 full collections in this loop while the
			// managed heap grew 2.5 -> 9 GB. Disabled, not Manual: measured, Manual still collects in
			// a loaded game once the heap is full.
			bool nogc = FastLoadOptions.AsyncLoadNoGc;
			int gcBefore = GC.CollectionCount(0);
			if (nogc)
			{
				UnityEngine.Scripting.GarbageCollector.GCMode = UnityEngine.Scripting.GarbageCollector.Mode.Disabled;
				gcHeld = true;
				PerfLog.Info("asyncload: GC disabled for the object loop" + (FastLoadOptions.AsyncLoadGcAfterLoop ? "" : " and the Start() replay") + " (was " + gcBefore + " collections)");
			}
			// Baseline: the main menu's own footprint, so the load's cost is a delta and not a
			// total that quietly includes everything the game already had.
			MemLine("the start of the object loop");
			double loopStart = Elapsed();
			long frameStart = Clock.ElapsedTicks;
			int groups = reader.ReadInt32();
			for (int j = 0; j < groups; j++)
			{
				string name = reader.ReadKleiString();
				int count = reader.ReadInt32();
				int bytes = reader.ReadInt32();
				Tag key = TagManager.Create(name);
				if (!prefabMap.TryGetValue(key, out GameObject prefab))
				{
					DebugUtil.LogWarningArgs("Could not find prefab '" + name + "'");
					reader.SkipBytes(bytes);
					objectsDone += count;
					recordIndex += count;
					continue;
				}
				sceneObjects[key] = new List<SaveLoadRoot>(count);
				bool batched = batcher != null && Batcher.Batchable(prefab, deferObjects ? keptPlan[j].Value : count);
				GameObject[] clones = null;
				int cloneAt = 0;
				for (int k = 0; k < count; k++)
				{
					if (deferObjects)
					{
						int deferredEnd;
						try
						{
							deferredEnd = DeferredObject.DeferredEnd(recordIndex, reader.Position);
						}
						catch (FormatException e)
						{
							UnityEngine.Debug.LogError("asyncload: " + e.Message);
							c.ok = false;
							batcher?.Abort();
							yield break;
						}
						if (deferredEnd >= 0)
						{
							reader.SkipBytes(deferredEnd - reader.Position);
							recordIndex++;
							objectsDone++;
							continue;
						}
					}
					if (batched && (clones == null || cloneAt == clones.Length))
					{
						AsyncInstantiateOperation<GameObject> op = batcher.Next();
						if (op == null)
						{
							UnityEngine.Debug.LogError("asyncload: batch plan out of step at [" + name + "]");
							c.ok = false;
							batcher.Abort();
							yield break;
						}
						long w0 = Clock.ElapsedTicks;
						while (!op.isDone)
						{
							Status("loading objects", string.Format("{0:N0} / {1:N0} objects; waiting for Unity to clone {2}", objectsDone, objectsTotal, name));
							yield return null;
						}
						waitTicks += Clock.ElapsedTicks - w0;
						frameStart = Clock.ElapsedTicks;
						clones = op.Result;
						cloneAt = 0;
						batcher.Pump();
					}
					SaveLoadRoot root = batched ? LoadClone(clones[cloneAt++], prefab, name, reader) : LoadOne(prefab, name, reader);
					if (SaveManager.DEBUG_OnlyLoadThisCellsObjects == -1 && root == null)
					{
						UnityEngine.Debug.LogError("Error loading data [" + name + "]");
						c.ok = false;
						batcher?.Abort();
						yield break;
					}
					// The in-game half of the extent gate: Klei's reader
					// and ObjectRecord.Scan must agree on where every built record ends, or every
					// deferred offset after it is wrong.
					if (deferObjects && reader.Position != DeferredObject.ExpectedEnd(recordIndex))
					{
						UnityEngine.Debug.LogError("asyncload: [" + name + "] record " + recordIndex + " ended at " + reader.Position + ", ObjectRecord.Scan said " + DeferredObject.ExpectedEnd(recordIndex));
						c.ok = false;
						batcher?.Abort();
						yield break;
					}
					recordIndex++;
					objectsDone++;
					if (MsSince(frameStart) > FrameBudgetMs)
					{
						long c0 = Clock.ElapsedTicks;
						if (nogc && ++capChecks % 25 == 0)
						{
							CapCheck("object " + objectsDone.ToString("N0"));
						}
						long c1 = Clock.ElapsedTicks;
						capTicks += c1 - c0;
						double s = Elapsed() - loopStart;
						double rate = s > 0 ? objectsDone / s : 0;
						Status("loading objects", string.Format("{0:N0} / {1:N0} objects ({2:0.0}%)   {3:N0}/s   ETA {4}\ngroup {5} of {6}: {7}  {8:N0} / {9:N0}",
							objectsDone, objectsTotal, objectsTotal > 0 ? 100.0 * objectsDone / objectsTotal : 0, rate,
							rate > 0 ? Clock2((objectsTotal - objectsDone) / rate) : "--:--", j + 1, groups, name, k + 1, count));
						long y0 = Clock.ElapsedTicks;
						statusTicks += y0 - c1;
						yield return null;
						frameStart = Clock.ElapsedTicks;
						yieldTicks += frameStart - y0;
						loopYields++;
					}
				}
			}
			FastLoadInternal.End();
			long privNow = PrivateBytes(out _);
			double f = Stopwatch.Frequency;
			PerfLog.Info(string.Format("asyncload: {0:N0} objects in {1:0.0} s; {2} collections during the loop; private {3:0.00} GB, managed {4:0.00} GB at its end",
				objectsDone, Elapsed() - loopStart, GC.CollectionCount(0) - gcBefore, privNow / 1e9, GC.GetTotalMemory(false) / 1e9));
			MemLine("the end of the object loop");
			if (batcher != null)
			{
				AsyncInstantiateOperation.SetIntegrationTimeMS(integrationWas);
				batcher.Abort();
				batcher = null;
			}
			PerfLog.Info(string.Format("asyncload: loop split: instantiate {0:0.0} s / {1:0.00} GB, activate {2:0.0} s / {3:0.00} GB, deserialize {4:0.0} s / {5:0.00} GB, waiting on InstantiateAsync {6:0.0} s ({7:N0} objects batched), other {8:0.0} s",
				instTicks / f, instBytes / 1e9, actTicks / f, actBytes / 1e9, deserTicks / f, deserBytes / 1e9, waitTicks / f, batchedObjects,
				Elapsed() - loopStart - (instTicks + actTicks + deserTicks + waitTicks) / f));
			PerfLog.Info(string.Format("asyncload: loop other split: off the coroutine {0:0.0} s over {1:N0} budget yields ({2:0.0} ms each), status text {3:0.00} s, cap checks {4:0.00} s, Alloc() {5:0.00} s over {6:N0} calls ({7:0.00} us each, mostly inside the three stamps above), rest {8:0.0} s",
				yieldTicks / f, loopYields, loopYields > 0 ? yieldTicks / f * 1000 / loopYields : 0, statusTicks / f, capTicks / f, allocTicks / f, allocCalls,
				allocCalls > 0 ? allocTicks / f * 1e6 / allocCalls : 0,
				Elapsed() - loopStart - (instTicks + actTicks + deserTicks + waitTicks + yieldTicks + statusTicks + capTicks) / f));
			{
				PerfLog.Info(string.Format("asyncload: FastDeser: {0:N0} mappings run, {1} plans ({2} fast fields, {3} composite, {4} generic); steps fast {5:N0} / composite {6:N0} / generic {7:N0} / property {8:N0} / skipped {9:N0}; callbacks {10:0.0} s; inside outermost mappings {11:0.0} s",
					FastDeser.Runs, FastDeser.PlansBuilt, FastDeser.FastFields, FastDeser.CompositeFields, FastDeser.GenericFields, FastDeser.FastSteps,
					FastDeser.CompositeSteps, FastDeser.GenericSteps, FastDeser.PropertySteps, FastDeser.SkipSteps, FastDeser.CallbackTicks / f, FastDeser.OuterTicks / f));
				if (TypeNameCache.Hits + TypeNameCache.Misses > 0)
				{
					PerfLog.Info(string.Format("asyncload: Type.GetType cache: {0:N0} hits, {1:N0} misses", TypeNameCache.Hits, TypeNameCache.Misses));
				}
				if (fastLoadInternal)
				{
					PerfLog.Info(string.Format("asyncload: LoadInternal split: lookup {0:0.0} s (GetComponents + layout {1:0.0} s), component managers {2:0.0} s, mapping deserialize {3:0.0} s, ISaveLoadableDetails {4:0.0} s; {5:N0} names ({6:N0} hits), {7:N0} layouts ({8:N0} hits), {9:N0} nested calls",
						FastLoadInternal.LookupTicks / f, FastLoadInternal.ComponentsTicks / f, FastLoadInternal.ManagerTicks / f, FastLoadInternal.MappingTicks / f, FastLoadInternal.DetailsTicks / f,
						LoadLookup.NameCount, LoadLookup.NameHits, LoadLookup.LayoutCount, LoadLookup.LayoutHits, FastLoadInternal.NestedCalls));
					var comps = new List<KeyValuePair<string, long[]>>(FastLoadInternal.ByComponent);
					comps.Sort((x, y) => y.Value[1].CompareTo(x.Value[1]));
					PerfLog.Info("asyncload: deserialize by component (n, ms): " + string.Join(", ", comps.GetRange(0, Math.Min(15, comps.Count)).ConvertAll(kv =>
						string.Format("{0} {1:N0} {2:0}", kv.Key, kv.Value[0], kv.Value[1] * 1000.0 / f)).ToArray()));
					// §7.10 split SaveGame's 0.657 GB between ReportManager and SolidConduitSerializer
					// by TIME, which is the inference §7.6 had to abandon for Navigator. This is the same
					// table in bytes, so the split is measured instead of assumed. Sorted separately,
					// because the heaviest component by time need not be the heaviest by allocation --
					// that mismatch is the whole reason the line exists.
					if (nogc)
					{
						var byBytes = new List<KeyValuePair<string, long[]>>(FastLoadInternal.ByComponent);
						byBytes.Sort((x, y) => y.Value[2].CompareTo(x.Value[2]));
						long totalBytes = 0L;
						foreach (var kv in byBytes) totalBytes += kv.Value[2];
						PerfLog.Info(string.Format("asyncload: deserialize allocation by component (n, MB), {0:0.00} GB over {1:N0} components: ",
							totalBytes / 1e9, byBytes.Count)
							+ string.Join(", ", byBytes.GetRange(0, Math.Min(15, byBytes.Count)).ConvertAll(kv =>
								string.Format("{0} {1:N0} {2:0.0}", kv.Key, kv.Value[0], kv.Value[2] / 1e6)).ToArray()));
					}
					else
					{
						PerfLog.Info("asyncload: deserialize allocation by component unavailable: a GC.GetTotalMemory delta is an allocation count only while the collector is held");
					}
				}
				var props = new List<KeyValuePair<string, long>>(FastDeser.PropertyRuns);
				props.Sort((x, y) => y.Value.CompareTo(x.Value));
				PerfLog.Info("asyncload: FastDeser property steps: " + string.Join(", ", props.GetRange(0, Math.Min(8, props.Count)).ConvertAll(kv => kv.Key + " " + kv.Value.ToString("N0")).ToArray()));
			}
			LogTop("objects by prefab", prefabCost, n => n, 20);
			LogTop("objects by prefab, allocation", prefabCost, n => n, 12, byBytes: true);
			if (nogc && FastLoadOptions.AsyncLoadGcAfterLoop)
			{
				ReleaseGc("object loop");
			}
		}

		/// <summary>Safety cap for a shared PC (64 GB): the garbage a disabled GC leaves behind is
		/// unmeasured, so past NoGcCapBytes collect once and carry on; the next cap collection only
		/// after NoGcCapStepBytes more growth past what that one left.</summary>
		internal static void CapCheck(string at, long cap = 0)
		{
			if (cap <= 0)
			{
				cap = NoGcCapBytes;
			}
			long priv = PrivateBytes(out _);
			if (priv > cap && priv > capNextBytes)
			{
				MemLine("cap collection " + (capCollections + 1) + ", before");
				double t0 = Elapsed();
				double cpu0 = ProcessCpuSeconds();
				UnityEngine.Scripting.GarbageCollector.GCMode = UnityEngine.Scripting.GarbageCollector.Mode.Enabled;
				GC.Collect();
				UnityEngine.Scripting.GarbageCollector.GCMode = UnityEngine.Scripting.GarbageCollector.Mode.Disabled;
				double took = Elapsed() - t0;
				double cpu = ProcessCpuSeconds() - cpu0;
				PerfLog.Info(string.Format("asyncload: cap collection {0}: {1:0.00} s wall, {2:0.00} s process CPU ({3:0.0} cores busy)",
					capCollections + 1, took, cpu, took > 0 ? cpu / took : 0.0));
				capCollections++;
				MemLine("cap collection " + capCollections + ", after");
				capNextBytes = PrivateBytes(out _) + NoGcCapStepBytes;
				PerfLog.Warn(string.Format("asyncload: private {0:0.00} GB passed the {1:0} GB cap at {2}; collected in {3:0.00} s; next collection past {4:0.00} GB",
					priv / 1e9, cap / 1e9, at, took, capNextBytes / 1e9));
			}
		}

		/// <summary>CPU time of every thread in the process, for telling a parallel mark (markers busy,
		/// CPU >> wall) from a serial one.</summary>
		internal static double ProcessCpuSeconds()
		{
			try
			{
				using (var p = System.Diagnostics.Process.GetCurrentProcess())
				{
					return p.TotalProcessorTime.TotalSeconds;
				}
			}
			catch (Exception)
			{
				return 0;
			}
		}

		/// <summary>End of the no-GC window: collections back on and one full collection now.</summary>
		private static void ReleaseGc(string after)
		{
			if (!gcHeld)
			{
				return;
			}
			gcHeld = false;
			UnityEngine.Scripting.GarbageCollector.GCMode = UnityEngine.Scripting.GarbageCollector.Mode.Enabled;
			double t0 = Elapsed();
			GC.Collect();
			PerfLog.Info(string.Format("asyncload: GC re-enabled after the {0}; one collection took {1:0.00} s, managed now {2:0.00} GB; {3} cap collections",
				after, Elapsed() - t0, GC.GetTotalMemory(false) / 1e9, capCollections));
			// The decisive one: this is the only reading taken when collections are on and one has
			// just finished, so managed USED here is what the loaded colony actually retains.
			MemLine("the post-load collection");
		}

		/// <summary>SaveLoadRoot.Load(GameObject, IReader) and its overload, game SaveLoadRoot.cs
		/// 174-229, timed per phase. The cell-debug filter falls back to Klei's own call.</summary>
		private static SaveLoadRoot LoadOne(GameObject prefab, string name, IReader reader)
		{
			if (SaveManager.DEBUG_OnlyLoadThisCellsObjects > -1)
			{
				return SaveLoadRoot.Load(prefab, reader);
			}
			Vector3 position = reader.ReadVector3();
			Quaternion rotation = reader.ReadQuaternion();
			Vector3 scale = reader.ReadVector3();
			reader.ReadByte();
			long t0 = Clock.ElapsedTicks, a0 = Alloc();
			GameObject go = Util.KInstantiate(prefab, position, rotation, null, null, initialize_id: false);
			go.transform.localScale = scale;
			return Activate(go, name, reader, t0, a0, position);
		}

		/// <summary>LoadOne for a clone InstantiateAsync already made: the rest of Util.KInstantiate
		/// (the game's Util.cs, the 7-argument overload with initialize_id false), then the same
		/// activate and deserialize. The prefab is inactive, so nothing on the clone has woken yet
		/// and the transform is set before Awake, as Instantiate(original, position, rotation) does.</summary>
		private static SaveLoadRoot LoadClone(GameObject go, GameObject prefab, string name, IReader reader)
		{
			Vector3 position = reader.ReadVector3();
			Quaternion rotation = reader.ReadQuaternion();
			Vector3 scale = reader.ReadVector3();
			reader.ReadByte();
			long t0 = Clock.ElapsedTicks, a0 = Alloc();
			go.transform.SetPositionAndRotation(position, rotation);
			go.name = prefab.name;
			KPrefabID id = go.GetComponent<KPrefabID>();
			if (id != null)
			{
				id.InitializeTags(force_initialize: true);
				KPrefabID source = prefab.GetComponent<KPrefabID>();
				id.CopyTags(source);
				id.CopyInitFunctions(source);
				id.RunInstantiateFn();
			}
			go.transform.localScale = scale;
			batchedObjects++;
			return Activate(go, name, reader, t0, a0, position);
		}

		/// <summary>The whole-object deferral's drain: one deferred record built the loop's way -- LoadOne, or
		/// LoadClone on a clone InstantiateAsync already made, and FastLoadInternal for the record and
		/// anything it loads nested (Storage contents) -- in place of Klei's SaveLoadRoot.Load, which
		/// after the loop runs LoadInternal's string reads and per-entry lookups again. Safe to nest:
		/// a lookup can build a record in the middle of another record's load.</summary>
		internal static SaveLoadRoot LoadDeferred(GameObject prefab, string name, IReader reader, GameObject clone)
		{
			bool was = fastLoadInternal && FastLoadInternal.Resume();
			try
			{
				return clone != null ? LoadClone(clone, prefab, name, reader) : LoadOne(prefab, name, reader);
			}
			finally
			{
				if (fastLoadInternal && !was)
				{
					FastLoadInternal.End();
				}
			}
		}

		internal static int DrainBatchChunk => BatchChunk;
		internal static int DrainBatchMin => BatchMin;
		internal static int DrainBatchLookahead => BatchLookahead;
		internal static float DrainIntegrationMs => BatchIntegrationMs;
		internal static bool DrainBatchable(GameObject prefab, int count) => Batcher.Batchable(prefab, count);

		/// <summary>The per-prefab table for the drain alone: cleared when it begins (the loop has
		/// logged its own by then), logged when it ends.</summary>
		internal static void ResetPrefabCost() => prefabCost.Clear();
		internal static void LogPrefabCost(string what)
		{
			LogTop(what, prefabCost, n => n, 20);
			LogTop(what + ", allocation", prefabCost, n => n, 12, byBytes: true);
		}

		/// <summary>Running totals of LoadOne/LoadClone's three phases (Stopwatch ticks), for the drain's split.</summary>
		internal static void PhaseTicks(out long inst, out long act, out long deser)
		{
			inst = instTicks;
			act = actTicks;
			deser = deserTicks;
		}

		private static SaveLoadRoot Activate(GameObject go, string name, IReader reader, long t0, long a0, Vector3 pos)
		{
			long t1 = Clock.ElapsedTicks, a1 = Alloc();
			go.SetActive(true);
			long t2 = Clock.ElapsedTicks, a2 = Alloc();
			SaveLoadRoot root = go.GetComponent<SaveLoadRoot>();
			if (root != null)
			{
				try
				{
					if (!fastLoadInternal || !FastLoadInternal.TryLoad(go, reader)) LoadInternalFn(go, reader);
				}
				catch (ArgumentException ex)
				{
					DebugUtil.LogErrorArgs(go, "Failed to load SaveLoadRoot ", ex.Message, "\n", ex.StackTrace);
				}
			}
			else
			{
				UnityEngine.Debug.Log("missing SaveLoadRoot", go);
			}
			long t3 = Clock.ElapsedTicks, a3 = Alloc();
			instTicks += t1 - t0;
			actTicks += t2 - t1;
			deserTicks += t3 - t2;
			instBytes += a1 - a0;
			actBytes += a2 - a1;
			deserBytes += a3 - a2;
			if (!prefabCost.TryGetValue(name, out Cost cost))
			{
				prefabCost[name] = cost = new Cost();
			}
			cost.n++;
			cost.inst += t1 - t0;
			cost.act += t2 - t1;
			cost.deser += t3 - t2;
			cost.actBytes += a2 - a1;
			cost.bytes += a3 - a0;
			return root;
		}

		private static void LogTop<K>(string what, Dictionary<K, Cost> table, Func<K, string> label, int n, bool byBytes = false)
		{
			double f = Stopwatch.Frequency / 1000.0;
			var rows = new List<KeyValuePair<K, Cost>>(table);
			if (byBytes)
			{
				rows.Sort((a, b) => b.Value.bytes.CompareTo(a.Value.bytes));
			}
			else
			{
				rows.Sort((a, b) => (b.Value.inst + b.Value.act + b.Value.deser).CompareTo(a.Value.inst + a.Value.act + a.Value.deser));
			}
			var sb = new StringBuilder("asyncload: top " + what + " (ms total = instantiate/start + activate + deserialize):");
			for (int i = 0; i < rows.Count && i < n; i++)
			{
				Cost c = rows[i].Value;
				sb.AppendFormat("\n  {0,-44} n={1,9:N0} total {2,9:N0} = {3,8:N0} + {4,8:N0} + {5,8:N0}  ({6:0.000} ms each)  {7:0.000} GB ({8:0.000} in activate, {9:N0} KB each)",
					label(rows[i].Key), c.n, (c.inst + c.act + c.deser) / f, c.inst / f, c.act / f, c.deser / f,
					(c.inst + c.act + c.deser) / f / Math.Max(c.n, 1), c.bytes / 1e9, c.actBytes / 1e9, c.bytes / 1024.0 / Math.Max(c.n, 1));
				if (c.maxAt != null)
				{
					sb.AppendFormat("  max {0:0.00} ms ({1})", c.max / f, c.maxAt);
				}
			}
			PerfLog.Info(sb.ToString());
		}

		/// <summary>The object section's group table (name, count), read ahead of the loop from the
		/// raw bytes: each group carries its byte length, so the walk never touches an object.</summary>
		private static List<KeyValuePair<string, int>> PlanGroups(IReader reader)
		{
			var plan = new List<KeyValuePair<string, int>>();
			try
			{
				byte[] b = reader.RawBytes();
				int p = reader.Position;
				int groups = BitConverter.ToInt32(b, p);
				p += 4;
				for (int i = 0; i < groups; i++)
				{
					int len = BitConverter.ToInt32(b, p);
					p += 4;
					string name = len >= 0 ? Encoding.UTF8.GetString(b, p, len) : null;
					p += Math.Max(len, 0);
					int count = BitConverter.ToInt32(b, p);
					p += 8 + BitConverter.ToInt32(b, p + 4);
					plan.Add(new KeyValuePair<string, int>(name, count));
				}
			}
			catch (Exception e)
			{
				PerfLog.Warn("asyncload: could not read the group table ahead (" + e.Message + "); batching off");
				plan.Clear();
			}
			return plan;
		}

		/// <summary>Issues InstantiateAsync for the batchable runs, in load order, keeping up to
		/// BatchLookahead clones queued. The loop takes the operations back in the same order.</summary>
		private sealed class Batcher
		{
			private readonly List<KeyValuePair<GameObject, int>> chunks = new List<KeyValuePair<GameObject, int>>();
			private readonly Queue<KeyValuePair<AsyncInstantiateOperation<GameObject>, int>> queued = new Queue<KeyValuePair<AsyncInstantiateOperation<GameObject>, int>>();
			private int next, queuedObjects;
			public int Objects { get; }
			public int Chunks => chunks.Count;

			public static bool Batchable(GameObject prefab, int count) => prefab != null && !prefab.activeSelf && count >= BatchMin;

			public Batcher(List<KeyValuePair<string, int>> plan, Dictionary<Tag, GameObject> prefabMap)
			{
				foreach (var g in plan)
				{
					if (g.Key == null || !prefabMap.TryGetValue(TagManager.Create(g.Key), out GameObject prefab) || !Batchable(prefab, g.Value))
					{
						continue;
					}
					for (int left = g.Value; left > 0; left -= BatchChunk)
					{
						chunks.Add(new KeyValuePair<GameObject, int>(prefab, Math.Min(left, BatchChunk)));
					}
					Objects += g.Value;
				}
			}

			public void Pump()
			{
				while (next < chunks.Count && queuedObjects < BatchLookahead)
				{
					var ch = chunks[next++];
					queued.Enqueue(new KeyValuePair<AsyncInstantiateOperation<GameObject>, int>(UnityEngine.Object.InstantiateAsync(ch.Key, ch.Value), ch.Value));
					queuedObjects += ch.Value;
				}
			}

			public AsyncInstantiateOperation<GameObject> Next()
			{
				if (queued.Count == 0)
				{
					Pump();
					if (queued.Count == 0)
					{
						return null;
					}
				}
				var q = queued.Dequeue();
				queuedObjects -= q.Value;
				return q.Key;
			}

			/// <summary>Cancels what is still queued and destroys clones nobody took.</summary>
			public void Abort()
			{
				while (queued.Count > 0)
				{
					AsyncInstantiateOperation<GameObject> op = queued.Dequeue().Key;
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
							}
						}
					}
				}
				next = chunks.Count;
				queuedObjects = 0;
			}
		}

		/// <summary>SaveLoader.Load(IReader) 434-452 and Load(string) 931-975.</summary>
		private static void LoadEpilogue(Ctx c)
		{
			SaveLoader loader = c.loader;
			var streamed = RootField<Dictionary<string, byte[]>>(c.root, "streamed");
			Grid.Visible = streamed["GridVisible"];
			if (streamed.ContainsKey("GridSpawnable"))
			{
				Grid.Spawnable = streamed["GridSpawnable"];
			}
			Grid.Damage = (float[])BytesToFloat.Invoke(loader, new object[] { streamed["GridDamage"] });
			Game.Instance.Load(c.deserializer);
			CameraSaveData.Load(new FastReader(streamed["Camera"]));
			ClusterManager.Instance.InitializeWorldGrid();
			SimMessages.DefineWorldOffsets(ClusterManager.Instance.WorldContainers.Select(container => new SimMessages.WorldOffsetData
			{
				worldOffsetX = container.WorldOffset.x,
				worldOffsetY = container.WorldOffset.y,
				worldSizeX = container.WorldSize.x,
				worldSizeY = container.WorldSize.y
			}).ToList());

			KCrashReporter.MOST_RECENT_SAVEFILE = c.path;
			SaveGame.GameInfo info = loader.GameInfo;
			if (info.isAutoSave && !string.IsNullOrEmpty(info.originalSaveName))
			{
				string original = SaveLoader.GetOriginalSaveFileName(info.originalSaveName);
				string target;
				if (SaveLoader.IsSaveCloud(c.path))
				{
					string cloud = SaveLoader.GetCloudSavePrefix();
					target = cloud == null
						? Path.Combine(Path.GetDirectoryName(c.path).Replace("auto_save", ""), info.baseName, original)
						: Path.Combine(cloud, info.baseName, original);
				}
				else
				{
					target = Path.Combine(SaveLoader.GetSavePrefix(), info.baseName, original);
				}
				if (target != null)
				{
					SaveLoader.SetActiveSaveFilePath(target);
				}
			}
			Stats.Print();
			DebugUtil.LogArgs("Loaded", "[" + c.path + "]");
			Klei.WorldDetailSave d = loader.clusterDetailSave;
			DebugUtil.LogArgs("World Seeds", "[" + d.globalWorldSeed + "/" + d.globalWorldLayoutSeed + "/" + d.globalTerrainSeed + "/" + d.globalNoiseSeed + "]");
			GC.Collect();
		}

		// --- the loading screen -----------------------------------------------------------------

		private static void BuildScreen()
		{
			var canvasGo = new GameObject("OniPerf.AsyncLoad.Canvas");
			canvasGo.transform.SetParent(host.transform, false);
			Canvas canvas = canvasGo.AddComponent<Canvas>();
			canvas.renderMode = RenderMode.ScreenSpaceOverlay;
			canvas.sortingOrder = 32000;
			CanvasScaler scaler = canvasGo.AddComponent<CanvasScaler>();
			scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
			scaler.referenceResolution = new Vector2(1920, 1080);
			scaler.matchWidthOrHeight = 1f;

			var bg = new GameObject("bg");
			bg.transform.SetParent(canvasGo.transform, false);
			Image img = bg.AddComponent<Image>();
			img.color = new Color(0.66f, 0.67f, 0.72f, 1f);
			Stretch(bg.GetComponent<RectTransform>(), Vector2.zero, Vector2.one);

			Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
			var title = new GameObject("title");
			title.transform.SetParent(canvasGo.transform, false);
			Text t = title.AddComponent<Text>();
			t.font = font;
			t.fontSize = 56;
			t.fontStyle = FontStyle.Bold;
			t.alignment = TextAnchor.MiddleCenter;
			t.color = new Color(0.93f, 0.93f, 0.95f, 1f);
			t.text = "LOADING...";
			Stretch(title.GetComponent<RectTransform>(), new Vector2(0f, 0.52f), new Vector2(1f, 0.62f));

			var body = new GameObject("detail");
			body.transform.SetParent(canvasGo.transform, false);
			text = body.AddComponent<Text>();
			text.font = font;
			text.fontSize = 26;
			text.alignment = TextAnchor.UpperCenter;
			text.color = new Color(0.20f, 0.21f, 0.25f, 1f);
			text.horizontalOverflow = HorizontalWrapMode.Overflow;
			text.verticalOverflow = VerticalWrapMode.Overflow;
			Stretch(body.GetComponent<RectTransform>(), new Vector2(0f, 0.20f), new Vector2(1f, 0.50f));
			Refresh();
		}

		private static void Stretch(RectTransform rt, Vector2 min, Vector2 max)
		{
			rt.anchorMin = min;
			rt.anchorMax = max;
			rt.offsetMin = Vector2.zero;
			rt.offsetMax = Vector2.zero;
		}

		private static void Status(string phase, string more = "")
		{
			status = phase;
			detail = more;
			Refresh();
		}

		private static void Refresh()
		{
			if (text == null)
			{
				return;
			}
			long priv;
			using (Process p = Process.GetCurrentProcess())
			{
				priv = p.PrivateMemorySize64;
			}
			text.text = string.Format("{0}   ({1})\n{2}\n\nmemory: private {3:0.00} GB   managed {4:0.00} GB   gc {5}/{6}/{7}",
				status, Clock2(Elapsed()), detail, priv / 1e9, GC.GetTotalMemory(false) / 1e9,
				GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
		}

		private static void RestoreCameras()
		{
			var line = new StringBuilder("asyncload: camera masks restored:");
			foreach (var kv in cameraMasks)
			{
				if (kv.Key != null && kv.Key.cullingMask == 0)
				{
					kv.Key.cullingMask = kv.Value;
				}
				if (kv.Key != null)
				{
					line.Append(' ').Append(kv.Key.name).Append('=').Append(kv.Key.cullingMask.ToString("x"));
				}
			}
			cameraMasks.Clear();
			PerfLog.Info(line.ToString());
		}

		// Game.OnSpawn (held until the Start() replay) builds CameraController, whose OnPrefabInit
		// derives baseCamera's mask from Camera.main's (the game's CameraController). With
		// Camera.main zeroed, baseCamera was left drawing only Construction, and RestoreCameras
		// never knew it: the world stayed black after the load. So Klei builds
		// its cameras from the real masks, and every camera it leaves is recorded and zeroed.
		private static void CameraInitPrefix()
		{
			if (!Busy)
			{
				return;
			}
			foreach (var kv in cameraMasks)
			{
				if (kv.Key != null)
				{
					kv.Key.cullingMask = kv.Value;
				}
			}
		}

		private static void CameraInitPostfix(CameraController __instance)
		{
			if (!Busy)
			{
				return;
			}
			var all = new HashSet<Camera>(Camera.allCameras);
			all.UnionWith(__instance.GetComponentsInChildren<Camera>(true));
			all.UnionWith(cameraMasks.Keys);
			foreach (Camera cam in all)
			{
				if (cam != null)
				{
					cameraMasks[cam] = cam.cullingMask;
					cam.cullingMask = 0;
				}
			}
		}

		private static double Elapsed() => (Clock.ElapsedTicks - startTicks) / (double)Stopwatch.Frequency;

		private static double MsSince(long ticks) => (Clock.ElapsedTicks - ticks) * 1000.0 / Stopwatch.Frequency;

		private static string Clock2(double s)
		{
			if (double.IsNaN(s) || double.IsInfinity(s) || s < 0)
			{
				return "--:--";
			}
			int t = (int)s;
			return string.Format("{0}:{1:00}", t / 60, t % 60);
		}
	}

	/// <summary>Coroutine host; lives outside the backend scene's lifecycle.</summary>
	internal sealed class AsyncLoadHost : MonoBehaviour
	{
	}
}
