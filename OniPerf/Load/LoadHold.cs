using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using HarmonyLib;

namespace OniPerf
{
	/// <summary>
	/// Times every save load and, with `loadGcHold`, holds the garbage collector off while the
	/// game loads a colony.
	///
	/// **Why.** A save load builds the whole colony: on a large colony that is hundreds of thousands
	/// of objects and many gigabytes of managed heap, nearly all of it still live when the load
	/// ends. The collector does not know that. It runs whenever enough has been allocated, so it
	/// runs again and again during the load, and each run walks a heap that keeps getting larger
	/// while freeing almost nothing. Holding it off lets the load allocate straight through, and one
	/// collection at the end does what the many would have done.
	///
	/// **When.** From the start of <c>SaveLoader.Load(string)</c> until the second frame after it
	/// returns: objects built by the load run their <c>Start()</c> in the frame after it, and they
	/// allocate too. Then collections are switched back on and one full collection runs. The game
	/// itself ends its load with a full collection, and while the hold is on that one does nothing,
	/// so the total stays one.
	///
	/// **Memory.** Without collections the process grows by what the collector would have freed.
	/// So the hold has a cap: once the process's private memory passes it, one collection runs and
	/// the hold carries on, and the next only after a further step of growth. The cap is half the
	/// installed memory, at most 24 GB, and the step a sixth of the cap. Below 24 GB installed the
	/// hold does not switch on at all, and the load runs as it would without the mod. A load that
	/// fails ends the hold at once.
	///
	/// Without `loadGcHold` only the timing runs: one line in the log per load.
	/// </summary>
	internal static class LoadHold
	{
		internal const double MinRamGb = 24.0;
		private const double CapFraction = 0.5;
		private const double MaxCapGb = 24.0;
		private const int CapCheckEvery = 1024;
		private const int FramesAfterLoad = 2;

		internal static bool HoldWanted;
		/// <summary>0: the installed memory. Otherwise the installed memory to assume (for testing).</summary>
		internal static double AssumeRamGb;

		private static bool held, waitingForFrames;
		private static int framesSeen;
		private static long capBytes, stepBytes, nextCapBytes, peakPrivate;

		/// <summary>The hold's cap in private bytes; 0 when the hold is off (not wanted, or too little memory).</summary>
		internal static long CapBytes => capBytes;
		/// <summary>The growth after a cap collection before the next one.</summary>
		internal static long StepBytes => stepBytes;
		private static int capCollections, objects;
		private static int gcCountAtStart;
		private static long loadStart;
		private static double loadSeconds;
		private static string ramNote;

		/// <summary>Lines other load fixes add to the load report (each returns null when it has nothing).</summary>
		internal static Func<string> Reports;

		public static void Install(Harmony harmony)
		{
			var load = AccessTools.Method(typeof(SaveLoader), "Load", new[] { typeof(string) });
			var root = AccessTools.Method(typeof(SaveLoadRoot), "Load", new[] { typeof(UnityEngine.GameObject), typeof(IReader) });
			var late = AccessTools.DeclaredMethod(typeof(Game), "LateUpdate");
			if (load == null || late == null)
			{
				PerfLog.Warn("loadhold: SaveLoader.Load(string) or Game.LateUpdate not found; off");
				return;
			}
			harmony.Patch(load, prefix: new HarmonyMethod(typeof(LoadHold), nameof(LoadPrefix)),
				postfix: new HarmonyMethod(typeof(LoadHold), nameof(LoadPostfix)));
			harmony.Patch(late, postfix: new HarmonyMethod(typeof(LoadHold), nameof(LateUpdatePostfix)));
			if (HoldWanted && root != null)
			{
				harmony.Patch(root, postfix: new HarmonyMethod(typeof(LoadHold), nameof(ObjectPostfix)));
			}
			double ram = InstalledRamGb();
			ramNote = AssumeRamGb > 0
				? string.Format(CultureInfo.InvariantCulture, "assuming {0:0} GB installed (config; {1:0.0} GB detected)", AssumeRamGb, ram)
				: string.Format(CultureInfo.InvariantCulture, "{0:0.0} GB installed", ram);
			double useRam = AssumeRamGb > 0 ? AssumeRamGb : ram;
			if (HoldWanted && useRam >= MinRamGb)
			{
				double capGb = Math.Min(MaxCapGb, useRam * CapFraction);
				capBytes = (long)(capGb * 1e9);
				stepBytes = capBytes / 6;
				PerfLog.Info(string.Format(CultureInfo.InvariantCulture,
					"loadhold: installed; collector held during a load, cap {0:0.0} GB private, step {1:0.0} GB ({2})",
					capBytes / 1e9, stepBytes / 1e9, ramNote));
			}
			else
			{
				if (HoldWanted)
				{
					PerfLog.Info(string.Format(CultureInfo.InvariantCulture,
						"loadhold: collector NOT held: {0}, the hold needs {1:0} GB; load timing only", ramNote, MinRamGb));
				}
				HoldWanted = false;
				capBytes = 0;
				stepBytes = 0;
			}
		}

		private static void LoadPrefix()
		{
			loadStart = Stopwatch.GetTimestamp();
			waitingForFrames = false;
			framesSeen = 0;
			objects = 0;
			capCollections = 0;
			peakPrivate = 0;
			gcCountAtStart = GC.CollectionCount(0);
			ConveyorPaths.NewColony();
			if (HoldWanted && !held)
			{
				nextCapBytes = capBytes;
				UnityEngine.Scripting.GarbageCollector.GCMode = UnityEngine.Scripting.GarbageCollector.Mode.Disabled;
				held = true;
			}
		}

		private static void LoadPostfix(bool __result)
		{
			loadSeconds = Seconds(loadStart);
			if (!__result)
			{
				Release("a load that failed");
				PerfLog.Warn(string.Format(CultureInfo.InvariantCulture, "load: failed after {0:0.00} s", loadSeconds));
				return;
			}
			CapCheck();
			waitingForFrames = true;
		}

		private static void ObjectPostfix()
		{
			if (held && ++objects % CapCheckEvery == 0)
			{
				CapCheck();
			}
		}

		private static void LateUpdatePostfix()
		{
			if (!waitingForFrames)
			{
				return;
			}
			if (held)
			{
				CapCheck();
			}
			if (++framesSeen < FramesAfterLoad)
			{
				return;
			}
			waitingForFrames = false;
			double tillRelease = Seconds(loadStart);
			int gcs = GC.CollectionCount(0) - gcCountAtStart;
			double collect = held ? Release("the load") : 0.0;
			string line = string.Format(CultureInfo.InvariantCulture,
				"load: SaveLoader.Load {0:0.00} s, {1:0.00} s to its second frame; {2} collections during it; process peak private {3:0.00} GB",
				loadSeconds, tillRelease, gcs, PeakPrivateBytes() / 1e9);
			if (capBytes > 0)
			{
				line += string.Format(CultureInfo.InvariantCulture,
					"; collector held: {0} cap collections, peak private {1:0.00} GB, final collection {2:0.00} s",
					capCollections, peakPrivate / 1e9, collect);
			}
			PerfLog.Info(line);
			if (Reports != null)
			{
				foreach (Func<string> r in Reports.GetInvocationList())
				{
					string more = r();
					if (more != null)
					{
						PerfLog.Info(more);
					}
				}
			}
		}

		private static void CapCheck()
		{
			long priv = PrivateBytes();
			if (priv > peakPrivate)
			{
				peakPrivate = priv;
			}
			if (!held || priv <= nextCapBytes)
			{
				return;
			}
			long t0 = Stopwatch.GetTimestamp();
			UnityEngine.Scripting.GarbageCollector.GCMode = UnityEngine.Scripting.GarbageCollector.Mode.Enabled;
			GC.Collect();
			UnityEngine.Scripting.GarbageCollector.GCMode = UnityEngine.Scripting.GarbageCollector.Mode.Disabled;
			capCollections++;
			long after = PrivateBytes();
			nextCapBytes = Math.Max(capBytes, after + stepBytes);
			PerfLog.Info(string.Format(CultureInfo.InvariantCulture,
				"loadhold: private {0:0.00} GB passed the cap; collected in {1:0.00} s, {2:0.00} GB after; next past {3:0.00} GB",
				priv / 1e9, Seconds(t0), after / 1e9, nextCapBytes / 1e9));
		}

		/// <summary>Collections back on and one full collection; its time in seconds.</summary>
		private static double Release(string after)
		{
			if (!held)
			{
				return 0.0;
			}
			held = false;
			UnityEngine.Scripting.GarbageCollector.GCMode = UnityEngine.Scripting.GarbageCollector.Mode.Enabled;
			long t0 = Stopwatch.GetTimestamp();
			GC.Collect();
			return Seconds(t0);
		}

		private static double Seconds(long since)
		{
			return (Stopwatch.GetTimestamp() - since) / (double)Stopwatch.Frequency;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct MemoryStatusEx
		{
			public uint Length;
			public uint MemoryLoad;
			public ulong TotalPhys;
			public ulong AvailPhys;
			public ulong TotalPageFile;
			public ulong AvailPageFile;
			public ulong TotalVirtual;
			public ulong AvailVirtual;
			public ulong AvailExtendedVirtual;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct ProcessMemoryCountersEx
		{
			public uint cb;
			public uint PageFaultCount;
			public UIntPtr PeakWorkingSetSize;
			public UIntPtr WorkingSetSize;
			public UIntPtr QuotaPeakPagedPoolUsage;
			public UIntPtr QuotaPagedPoolUsage;
			public UIntPtr QuotaPeakNonPagedPoolUsage;
			public UIntPtr QuotaNonPagedPoolUsage;
			public UIntPtr PagefileUsage;
			public UIntPtr PeakPagefileUsage;
			public UIntPtr PrivateUsage;
		}

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

		[DllImport("kernel32.dll")]
		private static extern IntPtr GetCurrentProcess();

		[DllImport("psapi.dll", SetLastError = true)]
		private static extern bool GetProcessMemoryInfo(IntPtr process, out ProcessMemoryCountersEx counters, int size);

		/// <summary>Installed physical memory in GB, or 0 when it cannot be read (then the hold stays off).</summary>
		private static double InstalledRamGb()
		{
			try
			{
				var s = new MemoryStatusEx { Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx)) };
				return GlobalMemoryStatusEx(ref s) ? s.TotalPhys / 1e9 : 0.0;
			}
			catch (Exception)
			{
				return 0.0;
			}
		}

		private static long PrivateBytes()
		{
			return MemoryCounters(out ProcessMemoryCountersEx c) ? (long)c.PrivateUsage.ToUInt64() : 0;
		}

		/// <summary>The most private memory the process has held since it started.</summary>
		private static long PeakPrivateBytes()
		{
			return MemoryCounters(out ProcessMemoryCountersEx c) ? (long)c.PeakPagefileUsage.ToUInt64() : 0;
		}

		private static bool MemoryCounters(out ProcessMemoryCountersEx c)
		{
			c = default(ProcessMemoryCountersEx);
			try
			{
				return GetProcessMemoryInfo(GetCurrentProcess(), out c, Marshal.SizeOf(typeof(ProcessMemoryCountersEx)));
			}
			catch (Exception)
			{
				return false;
			}
		}
	}
}
