using HarmonyLib;

namespace OniPerf
{
	/// <summary>
	/// Makes the game's core count readable from anywhere, which is what lets Fast Track's
	/// asynchronous work run at all on this build.
	///
	/// <para>The failure it fixes: <c>CPUBudget.coreCount</c> reads
	/// <c>UnityEngine.SystemInfo.processorCount</c> on every call, and Unity 6000.3.5f2 refuses
	/// that call from a MonoBehaviour constructor or field initialiser — it throws
	/// "GetProcessorCount is not allowed to be called from a MonoBehaviour constructor (or
	/// instance field initializer), call it in Awake or Start instead", because at that point
	/// the object is still being deserialised and has no engine-side counterpart. Fast Track
	/// 0.18.5's <c>AsyncJobManager..ctor</c> reads <c>CPUBudget.coreCount</c>, so the manager
	/// never constructs, everything that waits on its worker pool stalls (~10 s frames, 0.1 fps
	/// on the bench colony, 0.6 game seconds in 30 real ones) and
	/// <c>PropertyTextureUpdater.StartUpdate</c> then throws once a frame.</para>
	///
	/// <para><c>System.Environment.ProcessorCount</c> is the runtime's own count, not an engine
	/// call, so it is legal from a constructor, a field initialiser and any thread. It is read
	/// once here, when the mod loads, and the getter returns that value from then on. Klei's own
	/// callers are unaffected: they get the same number they would have got.</para>
	///
	/// <para>The tuning override (<c>Tuning.overrideCoreCount</c>, a private nested class read
	/// through <c>TuningData</c>) is deliberately not reproduced: it is -1 in the shipped data,
	/// and reading it would put a second lookup back on a path that has to work before the game
	/// is up. If a build ever ships an override, this patch would ignore it — stated here rather
	/// than discovered later.</para>
	///
	/// <para>Switched by the `cpuFix` key in config.json (on by default). With or without Fast
	/// Track installed, it changes nothing a caller can see except that the call no longer throws.</para>
	/// </summary>
	internal static class CpuFix
	{
		private static int cores;

		public static int Cores => cores;

		public static void Install(Harmony harmony)
		{
			cores = System.Environment.ProcessorCount;
			harmony.Patch(AccessTools.PropertyGetter(typeof(CPUBudget), nameof(CPUBudget.coreCount)),
				prefix: new HarmonyMethod(typeof(CpuFix), nameof(CoreCountPrefix)));
			PerfLog.Info("cpu-count fix: CPUBudget.coreCount now answers " + cores
				+ " from System.Environment.ProcessorCount, with no engine call");
		}

		private static bool CoreCountPrefix(ref int __result)
		{
			__result = cores;
			return false;
		}
	}
}
