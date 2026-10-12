using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace OniPerf
{
	/// <summary>
	/// Brains at the rate Klei tuned them for, not once per rendered frame.
	///
	/// Ported from the author's own earlier brain-rate mod, with one addition — see "spread" below.
	///
	/// **The mechanism, read out of `BrainScheduler`.** `BrainScheduler` is an `IRenderEveryTick`,
	/// so it runs once per *rendered frame*, and the work per call is a fixed number of brain
	/// probes: `DupeBrainGroup.Tuning.initialProbeCount` is 1, `CreatureBrainGroup`'s is 5. There is
	/// no `dt` term anywhere in `BrainGroup.RenderEveryTick` — `dt` is accepted and ignored. The
	/// class states the frame rate those counts were chosen against in its own constants: a
	/// 33.33 ms frame, 30 frames per second.
	///
	/// So the amount of brain work per second of play is proportional to the frame rate. At the
	/// 96 fps this save measures, ONI already performs 3.2x the chore searches per second that its
	/// own tuning intends; at the 300 fps target it would be 10x. Each probe is a full
	/// `ChoreConsumer.FindNextChore`: every registered `ChoreProvider` swept, every candidate run
	/// through its preconditions, the survivors sorted. This is the 1.61 ms of the paused frame
	/// that `BrainScheduler.RenderEveryTick` holds.
	///
	/// **Why this is not a "skip it when nothing changed" saving**. The budget is spent
	/// in `Time.unscaledDeltaTime`, real time, and it is spent at the same rate whether the colony
	/// is running or paused. A paused frame therefore gets exactly the same treatment as a running
	/// one, and the number a paused run reports is the number a speed-1 run should report. Klei's
	/// own `RenderEveryTick` has no pause test either, so this changes nothing about that.
	///
	/// The earlier mod additionally drops to 2 Hz while paused, on the grounds that only the UI can
	/// observe a chore search with no sim sub-ticks to act on it. That is a real and much larger
	/// win, and it is deliberately **not** the default here, because it is exactly the shape that
	/// flatters a paused measurement: it would score its best possible result on the only runs we
	/// are taking. <see cref="PausedHz"/> holds it, and this mod sets it to the running rate.
	///
	/// **Two shapes, because they differ where it matters at a 3.33 ms target.**
	///
	///   - `skip` is the earlier mod's: gate the whole `RenderEveryTick` on a real-time budget. The
	///     mean falls by the full ratio, but the frames that do run still pay all 1.61 ms, so p99
	///     does not move. At a 3.33 ms frame a 1.6 ms spike is half the budget.
	///   - `spread` (the addition, and what this mod uses): leave `RenderEveryTick` running every frame and scale
	///     the *probe count* instead, through `InitialProbeCount`. The loop's first act is
	///     `if (num == 0) break;`, so a zero allowance costs nothing, and a fractional allowance
	///     lands as one or two brains on most frames rather than six on one frame in three. Same
	///     mean, smaller spike.
	///
	/// Which one is better is a measurement, not an argument, so both are here and the run reports
	/// p50 and p99 for each.
	///
	/// Nothing about how a brain is chosen or what a probe does is touched, in either shape, so a
	/// duplicant makes the decisions it would make in a vanilla game at 30 fps.
	/// </summary>
	internal static class BrainRate
	{
		/// <summary>Set at load; testing also flips it mid-run for an A/B.</summary>
		public static bool Active;

		public enum Shape
		{
			Skip,
			Spread,
		}

		public static Shape Mode = Shape.Spread;

		/// <summary>Probes per second of real time while time is advancing. Klei's tuning is 30.</summary>
		public static float RunningHz = 30f;

		/// <summary>
		/// Probes per second while paused. Defaults to <see cref="RunningHz"/> so that a paused run
		/// measures the running behaviour and the number carries; the earlier mod's 2 Hz is a separate
		/// lever that only a paused frame can show, and it is priced separately.
		/// </summary>
		public static float PausedHz = 30f;

		private static bool installed;

		// skip mode
		private static float credit;
		private static bool wasPaused;
		private static int framesRun, framesSkipped;

		// spread mode: per brain group, because the two groups have different Klei counts (1 and 5)
		// and therefore different allowances on the same frame.
		private static readonly Dictionary<object, float> owed = new Dictionary<object, float>();
		private static readonly Dictionary<object, int> allowed = new Dictionary<object, int>();
		private static readonly Dictionary<object, int> kleiCount = new Dictionary<object, int>();
		private static long probesRun, probesKlei;

		/// <summary>
		/// The game drives Unity's own timeScale, so this is both the cheapest pause test available
		/// and the one that agrees with what the sim is doing -- SpeedControlScreen.IsPaused would be
		/// a UI object that need not exist.
		/// </summary>
		private static bool IsPaused => Time.timeScale <= 0f;

		public static void Install(Harmony harmony)
		{
			if (installed)
			{
				return;
			}
			MethodInfo tick = AccessTools.DeclaredMethod(typeof(BrainScheduler),
				nameof(BrainScheduler.RenderEveryTick), new[] { typeof(float) });
			if (tick == null)
			{
				PerfLog.Warn("brainrate: no BrainScheduler.RenderEveryTick; off");
				return;
			}
			try
			{
				harmony.Patch(tick,
					prefix: new HarmonyMethod(typeof(BrainRate), nameof(TickPrefix)) { priority = Priority.Last });
			}
			catch (Exception e)
			{
				PerfLog.Warn("brainrate: could not patch RenderEveryTick: " + e.Message);
				return;
			}

			// The probe count lives on two private nested types, and each overrides the abstract
			// InitialProbeCount separately, so both need their own patch.
			int counts = 0;
			foreach (string name in new[] { "BrainScheduler+DupeBrainGroup", "BrainScheduler+CreatureBrainGroup" })
			{
				Type t = AccessTools.TypeByName(name);
				MethodInfo m = t != null ? AccessTools.DeclaredMethod(t, "InitialProbeCount", Type.EmptyTypes) : null;
				if (m == null)
				{
					PerfLog.Warn("brainrate: no " + name + ".InitialProbeCount; spread mode will be partial");
					continue;
				}
				try
				{
					harmony.Patch(m, postfix: new HarmonyMethod(typeof(BrainRate), nameof(ProbeCountPostfix)));
					counts++;
				}
				catch (Exception e)
				{
					PerfLog.Warn("brainrate: could not patch " + name + ".InitialProbeCount: " + e.Message);
				}
			}
			installed = true;
			PerfLog.Info("brainrate: installed; " + counts + " of 2 probe counts hooked, mode=" + Mode
				+ " runningHz=" + RunningHz.ToString("0.##") + " pausedHz=" + PausedHz.ToString("0.##"));
		}

		/// <summary>
		/// Skip mode only: answers whether this frame carries a brain update, and consumes the
		/// budget if it does. Spread mode always returns true -- it changes the size of the update,
		/// not whether there is one.
		/// </summary>
		private static bool TickPrefix(BrainScheduler __instance)
		{
			if (!Active)
			{
				return true;
			}
			if (Mode == Shape.Spread)
			{
				Budget(__instance);
				return true;
			}

			bool paused = IsPaused;
			// Pausing and unpausing are the two moments the player is most likely to be watching a
			// duplicant, so spend a probe on the transition itself.
			if (paused != wasPaused)
			{
				wasPaused = paused;
				credit = 0f;
				framesRun++;
				return true;
			}
			float period = 1f / Math.Max(0.01f, paused ? PausedHz : RunningHz);
			credit += Time.unscaledDeltaTime;
			if (credit < period)
			{
				framesSkipped++;
				return false;
			}
			credit -= period;
			// A single long frame -- a save, a full GC, an alt-tab -- must not buy a burst of
			// catch-up updates it can never spend, since at most one runs per frame anyway.
			if (credit > period)
			{
				credit = period;
			}
			framesRun++;
			return true;
		}

		/// <summary>
		/// Spread mode: decide this frame's allowance per group, once, before the groups run.
		/// `InitialProbeCount` is called again by `CreatureBrainGroup.PostRenderEveryTick` in the
		/// same frame, and it must see the same number, so the decision cannot live in the postfix.
		/// </summary>
		private static void Budget(BrainScheduler scheduler)
		{
			if (scheduler == null)
			{
				return;
			}
			float hz = Math.Max(0.01f, IsPaused ? PausedHz : RunningHz);
			float dt = Time.unscaledDeltaTime;
			foreach (object group in scheduler.debugGetBrainGroups())
			{
				int klei;
				if (!kleiCount.TryGetValue(group, out klei))
				{
					// Not seen yet: the postfix records the real value the first time the group asks,
					// and until then the group runs exactly as Klei wrote it.
					continue;
				}
				probesKlei += klei;
				float have;
				owed.TryGetValue(group, out have);
				have += klei * hz * dt;
				int n = (int)have;
				if (n > klei)
				{
					// Never more work than vanilla would have done on this frame.
					n = klei;
				}
				have -= n;
				if (have > klei)
				{
					have = klei;
				}
				owed[group] = have;
				allowed[group] = n;
				probesRun += n;
			}
		}

		private static void ProbeCountPostfix(object __instance, ref int __result)
		{
			// A postfix sees the original's return value, so __result is Klei's own number here,
			// before this method replaces it. That is where the budget gets it from: calling
			// InitialProbeCount to ask would come straight back through this patch.
			kleiCount[__instance] = __result;
			if (!Active || Mode != Shape.Spread)
			{
				return;
			}
			if (allowed.TryGetValue(__instance, out int n))
			{
				__result = n;
			}
		}

		public static void Reset()
		{
			credit = 0f;
			framesRun = framesSkipped = 0;
			probesRun = probesKlei = 0;
			owed.Clear();
			allowed.Clear();
		}

		public static void LogSummary(string what)
		{
			if (Mode == Shape.Spread)
			{
				PerfLog.Info("brainrate: " + what + " spread -- " + probesRun + " brain probes where vanilla would have run "
					+ probesKlei + " ("
					+ (probesKlei > 0 ? (100.0 * probesRun / probesKlei).ToString("0.0") : "0") + "%)");
			}
			else
			{
				PerfLog.Info("brainrate: " + what + " skip -- " + framesRun + " frames ran brains, "
					+ framesSkipped + " skipped ("
					+ (framesRun + framesSkipped > 0
						? (100.0 * framesRun / (framesRun + framesSkipped)).ToString("0.0")
						: "0") + "%)");
			}
		}
	}
}
