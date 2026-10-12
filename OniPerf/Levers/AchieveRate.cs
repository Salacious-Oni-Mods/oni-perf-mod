using System;
using HarmonyLib;
using UnityEngine;

namespace OniPerf
{
	/// <summary>
	/// `ColonyAchievementTracker.RenderEveryTick`, one achievement re-checked per rendered frame,
	/// capped to <see cref="Hz"/> checks a second of real time.
	///
	/// **The mechanism, measured.** 0.095 ms of a 4.75 ms paused frame, from
	/// the RENDER_EVERY_TICK bucket. Each call takes the next achievement by `Enumerable.ElementAt`
	/// on a `Dictionary` (a walk from the start) and, unless it has already succeeded or failed,
	/// runs every requirement's check. The rate is the frame rate, so the faster the game renders
	/// the more often it asks the same questions: 200 fps re-checks the whole list about five times
	/// a second, 60 fps about once and a half.
	///
	/// **The lever.** The call runs at most <see cref="Hz"/> times a second of real time, so a
	/// newly met achievement is noticed within (achievement count / Hz) seconds instead of
	/// (achievement count / fps). Nothing else changes: the same round-robin, the same checks, the
	/// same unlock path. Not the paused-only saving: the cost is per frame at every speed.
	///
	/// Switched by the `achieveRate` key in config.json (on by default).
	/// </summary>
	internal static class AchieveRate
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
			var target = AccessTools.DeclaredMethod(typeof(ColonyAchievementTracker), "RenderEveryTick");
			if (target == null)
			{
				PerfLog.Warn("achieverate: no ColonyAchievementTracker.RenderEveryTick; off");
				return;
			}
			try
			{
				harmony.Patch(target, prefix: new HarmonyMethod(typeof(AchieveRate), nameof(Prefix)));
				installed = true;
			}
			catch (Exception e)
			{
				PerfLog.Warn("achieverate: could not patch: " + e.Message);
				return;
			}
			PerfLog.Info("achieverate: installed on ColonyAchievementTracker.RenderEveryTick");
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
			PerfLog.Info("achieverate: " + what + " -- " + ran + " ran, " + skipped + " skipped");
		}
	}
}
