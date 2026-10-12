using HarmonyLib;

namespace OniPerf
{
	/// <summary>
	/// While the defer-object drain builds the colony and the game is paused, the paused frame's
	/// own work that nothing can act on is cut, so the frame-rate target leaves the drain a bigger
	/// slice.
	///
	/// **The frame (measured over 3,000 drain frames on Euphoria,
	/// vsync off).** The paused game frame during the drain is ~12.4 ms beside a 4.9 ms drain slice. Three
	/// items in it run every paused frame for nothing:
	///
	///   - `BrainScheduler.RenderEveryTick` (1.50 ms): chore searches on Klei's unscaled clock. With
	///     no sim sub-ticks a dupe cannot act on what it finds, and the colony it searches is half
	///     built. Skipped.
	///   - `Game.SimEveryTick` (1.30 ms): paused, `Time.deltaTime` is 0, so all it does is
	///     `UnsafeSim200ms(0)`, a zero-length sim step that hands back the sim's queued callbacks
	///     and cell changes. Run every <see cref="SimEvery"/>th frame, so those still arrive.
	///   - `PropertyTextures.LateUpdate` (1.00 ms): the overlay property textures. Run every
	///     <see cref="TexEvery"/>th frame.
	///
	/// Unpaused nothing is cut: the colony runs on what has loaded (DrainPause.cs), and the drain's
	/// frame-rate controller shrinks its slice instead. Off unless the quiet drain.
	/// </summary>
	internal static class DrainQuiet
	{
		private const int SimEvery = 8;
		private const int TexEvery = 4;

		private static bool installed, draining;
		private static int simFrame, texFrame;
		private static long brainsSkipped, simSkipped, texSkipped;

		public static void Install(Harmony harmony)
		{
			if (installed)
			{
				return;
			}
			try
			{
				harmony.Patch(AccessTools.DeclaredMethod(typeof(BrainScheduler), nameof(BrainScheduler.RenderEveryTick), new[] { typeof(float) }),
					prefix: new HarmonyMethod(typeof(DrainQuiet), nameof(BrainPrefix)));
				harmony.Patch(AccessTools.DeclaredMethod(typeof(Game), "SimEveryTick"),
					prefix: new HarmonyMethod(typeof(DrainQuiet), nameof(SimPrefix)));
				harmony.Patch(AccessTools.DeclaredMethod(typeof(PropertyTextures), "LateUpdate"),
					prefix: new HarmonyMethod(typeof(DrainQuiet), nameof(TexPrefix)));
				installed = true;
				PerfLog.Info("drain-quiet: installed; while the drain runs paused: brains off, the sim stepped every "
					+ SimEvery + "th frame, property textures every " + TexEvery + "th");
			}
			catch (System.Exception e)
			{
				PerfLog.Warn("drain-quiet: could not patch: " + e.Message);
			}
		}

		/// <summary>When the drain begins.</summary>
		public static void Begin()
		{
			draining = installed;
			simFrame = texFrame = 0;
			brainsSkipped = simSkipped = texSkipped = 0;
		}

		/// <summary>On every way out of the drain.</summary>
		public static void End()
		{
			if (!draining)
			{
				return;
			}
			draining = false;
			PerfLog.Info("drain-quiet: skipped " + brainsSkipped + " brain ticks, " + simSkipped + " zero-length sim steps, "
				+ texSkipped + " property-texture updates");
		}

		private static bool Quiet() => draining && SpeedControlScreen.Instance != null && SpeedControlScreen.Instance.IsPaused;

		private static bool BrainPrefix()
		{
			if (!Quiet())
			{
				return true;
			}
			brainsSkipped++;
			return false;
		}

		private static bool SimPrefix(float dt)
		{
			// dt > 0 means time is moving (the pause is being lifted this frame): never skip that.
			if (!Quiet() || dt > 0f || ++simFrame % SimEvery == 0)
			{
				return true;
			}
			simSkipped++;
			return false;
		}

		private static bool TexPrefix()
		{
			if (!Quiet() || ++texFrame % TexEvery == 0)
			{
				return true;
			}
			texSkipped++;
			return false;
		}
	}
}
