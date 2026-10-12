using System;
using HarmonyLib;
using UnityEngine;

namespace OniPerf
{
	/// <summary>
	/// `AudioMixer.SetVisibleDuplicants`, the per-frame count of visible, moving and sleeping
	/// dupes, recounted at most <see cref="Hz"/> times a second of real time.
	///
	/// **The mechanism, measured.** `MixManager.Update` calls
	/// `AudioMixer.UpdatePersistentSnapshotParameters` every rendered frame at every speed, and
	/// that starts with `SetVisibleDuplicants`: for every live dupe a position read, a camera
	/// visibility test, `GetComponent&lt;Navigator&gt;` and, for the ones not moving,
	/// `GetComponent&lt;WorkerBase&gt;().GetSMI&lt;StaminaMonitor.Instance&gt;()`. 0.10 ms of a 3.6 ms
	/// paused frame with 200 dupes; the rest of `MixManager.Update` is 0.01.
	///
	/// **The lever.** Between recounts the three counts keep their last values (they live in
	/// `AudioMixer.visibleDupes`, which nothing else writes), and the FMOD parameters are still set
	/// from them every frame, so the time-driven Pulse parameter is untouched. What changes: the
	/// dupe-count attenuation follows the view up to 1/<see cref="Hz"/> s late, on parameters
	/// FMOD itself smooths. Not the paused-only saving: the cost is per frame at every speed.
	///
	/// Switched by the `mixRate` key in config.json (on by default).
	/// </summary>
	internal static class MixRate
	{
		public static bool Active;

		public const float Hz = 10f;

		private static float last = float.NegativeInfinity;
		private static long ran, skipped;
		private static bool installed;

		public static void Install(Harmony harmony)
		{
			if (installed)
			{
				return;
			}
			var target = AccessTools.DeclaredMethod(typeof(AudioMixer), "SetVisibleDuplicants");
			if (target == null)
			{
				PerfLog.Warn("mixrate: no AudioMixer.SetVisibleDuplicants; off");
				return;
			}
			try
			{
				harmony.Patch(target, prefix: new HarmonyMethod(typeof(MixRate), nameof(Prefix)));
				installed = true;
			}
			catch (Exception e)
			{
				PerfLog.Warn("mixrate: could not patch: " + e.Message);
				return;
			}
			PerfLog.Info("mixrate: installed on AudioMixer.SetVisibleDuplicants");
		}

		private static bool Prefix()
		{
			if (Active)
			{
				float now = Time.realtimeSinceStartup;
				if (now - last < 1f / Hz)
				{
					skipped++;
					return false;
				}
				last = now;
			}
			ran++;
			return true;
		}

		public static void Reset()
		{
			ran = skipped = 0;
		}

		public static void LogSummary(string what)
		{
			PerfLog.Info("mixrate: " + what + " -- " + ran + " ran, " + skipped + " skipped");
		}
	}
}
