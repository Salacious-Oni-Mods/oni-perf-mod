using System;
using HarmonyLib;
using UnityEngine;

namespace OniPerf
{
	/// <summary>
	/// Two Steam API calls that run every rendered frame, priced as two separable halves.
	///
	/// **The mechanism, measured.** Of a 6.7 ms paused frame:
	///
	///   - `SteamInput.RunFrame` = **0.49 ms**. `Global.Update` -> `GameInputManager.Update` ->
	///     `SteamInputInterpreter.Update` -> `CheckForControllerChange` calls it every frame, before
	///     `GetConnectedControllers` (cheap) and `ActivateActionSet` (cheap).
	///   - `SteamAPI.RunCallbacks` = **0.38 ms**, from `SteamManager.Update`, every frame.
	///
	/// **Half A, `skiprunframe`: the per-frame `RunFrame` throttled to 20 Hz while no pad is in use.**
	/// The first version skipped it outright on the premise that `SteamAPI_RunCallbacks` already
	/// performs it. **That premise is false for ONI** and an earlier version shipped broken: ONI calls
	/// `SteamInput.Init(bExplicitlyCallRunFrame: true)`, and the Steamworks header says that with
	/// `true` "you will need to manually call RunFrame each frame, otherwise Steam Input will updated
	/// when SteamAPI_RunCallbacks() is called". So `RunCallbacks` never refreshes Steam Input here, and
	/// skipping every per-frame `RunFrame` froze pad state and the connected-controller list after
	/// load.
	///
	/// Now: while `KInputManager.currentControllerIsGamepad` is set the call runs every frame,
	/// exactly as Klei's. Otherwise it runs at most <see cref="IdleHz"/> times a second, so a first
	/// pad press or stick push is seen within 50 ms, sets `currentControllerIsGamepad` in
	/// `SteamInputInterpreter.Update`, and every frame after runs at full rate. A keyboard or mouse
	/// event clears the flag (`KInputController`), which drops back to 20 Hz. The cost of the design:
	/// a pad tap shorter than 50 ms made while the mouse was the active device can be missed; a held
	/// press or a stick push cannot. Initialisation and `Reset` keep their calls.
	///
	/// **Half B, `RunCallbacks` at 30 Hz, on with the lever.** `SteamManager.Update` is
	/// nothing but `SteamAPI.RunCallbacks`, 0.45 ms a call, every frame. It dispatches Steam's own
	/// callbacks (overlay, achievements, UGC, call results), none of which needs to arrive within a
	/// frame, and in ONI it does not refresh Steam Input (see above), so capping it leaves pad
	/// latency alone. Its rate is the `steamPollCallbacksHz` key in config.json.
	///
	/// **Not a paused-only saving.** Both calls are per frame whatever the colony does.
	///
	/// **Not verified with a pad in hand.** Steam reports one connected controller on this machine
	/// (the first design, a hot-plug gate for an empty port, never fired because of it), but no run
	/// has pressed a button on it.
	///
	/// Switched by the `steamPoll` key in config.json (on by default); `steamPollCallbacksHz` sets half B's rate.
	/// </summary>
	internal static class SteamPoll
	{
		public static bool SkipRunFrame;
		public static float CallbacksHz;

		/// <summary>The per-frame RunFrame rate while no pad is the active controller.</summary>
		public const float IdleHz = 20f;

		/// <summary>Half B's rate when the lever is on and no other rate was asked for.</summary>
		public const float DefaultCallbacksHz = 30f;

		[ThreadStatic] private static bool insideCheck;
		private static float lastCallbacks = float.NegativeInfinity, lastRunFrame = float.NegativeInfinity;
		private static long runFrameSkipped, runFrameRan, callbacksRan, callbacksSkipped;
		private static bool installed;

		public static void Install(Harmony harmony)
		{
			if (installed)
			{
				return;
			}
			Type interp = AccessTools.TypeByName("SteamInputInterpreter");
			Type input = AccessTools.TypeByName("Steamworks.SteamInput");
			Type api = AccessTools.TypeByName("Steamworks.SteamAPI");
			var check = interp == null ? null : AccessTools.DeclaredMethod(interp, "CheckForControllerChange");
			var runFrame = input == null ? null : AccessTools.DeclaredMethod(input, "RunFrame");
			var callbacks = api == null ? null : AccessTools.DeclaredMethod(api, "RunCallbacks");
			if (check == null || runFrame == null || callbacks == null)
			{
				PerfLog.Warn("steampoll: missing CheckForControllerChange, SteamInput.RunFrame or SteamAPI.RunCallbacks; off");
				return;
			}
			try
			{
				harmony.Patch(check, prefix: new HarmonyMethod(typeof(SteamPoll), nameof(CheckPrefix)),
					finalizer: new HarmonyMethod(typeof(SteamPoll), nameof(CheckFinalizer)));
				harmony.Patch(runFrame, prefix: new HarmonyMethod(typeof(SteamPoll), nameof(RunFramePrefix)));
				harmony.Patch(callbacks, prefix: new HarmonyMethod(typeof(SteamPoll), nameof(CallbacksPrefix)));
				installed = true;
			}
			catch (Exception e)
			{
				PerfLog.Warn("steampoll: could not patch: " + e.Message);
				return;
			}
			PerfLog.Info("steampoll: installed on CheckForControllerChange, SteamInput.RunFrame, SteamAPI.RunCallbacks");
		}

		private static void CheckPrefix()
		{
			insideCheck = true;
		}

		private static Exception CheckFinalizer(Exception __exception)
		{
			insideCheck = false;
			return __exception;
		}

		private static bool RunFramePrefix()
		{
			if (SkipRunFrame && insideCheck && !KInputManager.currentControllerIsGamepad)
			{
				float now = Time.realtimeSinceStartup;
				if (now - lastRunFrame < 1f / IdleHz)
				{
					runFrameSkipped++;
					return false;
				}
				lastRunFrame = now;
			}
			runFrameRan++;
			return true;
		}

		private static bool CallbacksPrefix()
		{
			if (CallbacksHz <= 0f)
			{
				callbacksRan++;
				return true;
			}
			float now = Time.realtimeSinceStartup;
			if (now - lastCallbacks < 1f / CallbacksHz)
			{
				callbacksSkipped++;
				return false;
			}
			lastCallbacks = now;
			callbacksRan++;
			return true;
		}

		public static void Reset()
		{
			runFrameSkipped = runFrameRan = callbacksRan = callbacksSkipped = 0;
		}

		public static void LogSummary(string what)
		{
			PerfLog.Info("steampoll: " + what + " -- RunFrame " + runFrameRan + " ran, " + runFrameSkipped
				+ " skipped; RunCallbacks " + callbacksRan + " ran, " + callbacksSkipped + " skipped");
		}
	}
}
