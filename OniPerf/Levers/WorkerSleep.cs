using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace OniPerf
{
	/// <summary>
	/// Two per-dupe `Update`s that do nothing on most frames, disabled on the frames they would do
	/// nothing.
	///
	/// **The mechanism.** Every dupe carries a
	/// `StandardWorker` and a `WearableAccessorizer`, and Unity calls each one's `Update` every
	/// rendered frame, paused or not: 200 calls of each on the bench colony. Their bodies:
	///
	/// - `StandardWorker.Update`: `if (state == Working &amp;&amp; !surpressForceSyncOnUpdate)
	///   ForceSyncAnims()`, and `ForceSyncAnims` does nothing unless `Time.deltaTime &gt; 0`. So it
	///   does work only for a working dupe, unpaused.
	/// - `WearableAccessorizer.Update`: acts only while `waitingForOutfitChangeFX`, which
	///   `QueueOutfitChangedFX` sets and the Update itself clears.
	///
	/// Neither class, nor `WorkerBase`, overrides `OnCmpEnable`/`OnCmpDisable`, and
	/// `KMonoBehaviour`'s are empty, so toggling `enabled` only switches the Update call. Nothing in
	/// the game reads either component's `enabled`.
	///
	/// **The lever, event-driven.** A first version walked every dupe in a `Game.LateUpdate`
	/// postfix and cost as much as it saved. This one has no per-frame walk:
	///
	/// - A prefix on each `Update` disables its own component when the body would do nothing and
	///   skips it. For the worker, "nothing" is not working, suppressed, or `Time.timeScale == 0`:
	///   Unity makes a frame's `deltaTime` from the `timeScale` it finds at the frame's start, and
	///   ONI changes the scale only inside a frame, so a zero scale seen now means the next frame's
	///   `deltaTime` is zero too.
	/// - `StandardWorker.StartWork` (the only place `state` becomes Working) and
	///   `WearableAccessorizer.QueueOutfitChangedFX` re-enable their component at once.
	/// - A worker put to sleep while working, by the pause, is woken when the scale turns non-zero:
	///   a postfix on `SpeedControlScreen.OnChanged` (where ONI sets it), and a one-comparison check
	///   in a `Game.LateUpdate` postfix for any other path.
	///
	/// Every Update that would act still runs. Paused, every dupe's pair is off and the lever costs
	/// nothing per frame; at speed, only the idle dupes' workers are off, and the working ones pay
	/// one Harmony prefix each. Applies to every `StandardWorker` (rovers, fetch drones and
	/// `RemoteWorker` share the Update and `StartWork`).
	///
	/// Switched by the `workerSleep` key in config.json (on by default). Switching it off re-enables every
	/// component it disabled.
	/// </summary>
	internal static class WorkerSleep
	{
		private static bool active;

		private static readonly HashSet<Behaviour> asleep = new HashSet<Behaviour>();
		private static readonly HashSet<StandardWorker> pausedWorkers = new HashSet<StandardWorker>();

		private static AccessTools.FieldRef<StandardWorker, StandardWorker.State> stateRef;
		private static AccessTools.FieldRef<StandardWorker, bool> surpressRef;
		private static AccessTools.FieldRef<WearableAccessorizer, bool> waitingRef;
		private static long workerSlept, wearSlept, woken, unpauseWoken;
		private static bool installed;

		public static bool Active
		{
			get => active;
			set
			{
				if (active && !value)
				{
					WakeAll();
				}
				active = value;
			}
		}

		public static void Install(Harmony harmony)
		{
			if (installed)
			{
				return;
			}
			var workerUpdate = AccessTools.DeclaredMethod(typeof(StandardWorker), "Update");
			var wearUpdate = AccessTools.DeclaredMethod(typeof(WearableAccessorizer), "Update");
			var startWork = AccessTools.DeclaredMethod(typeof(StandardWorker), "StartWork");
			var queueFx = AccessTools.DeclaredMethod(typeof(WearableAccessorizer), "QueueOutfitChangedFX");
			var onChanged = AccessTools.DeclaredMethod(typeof(SpeedControlScreen), "OnChanged");
			var lateUpdate = AccessTools.DeclaredMethod(typeof(Game), "LateUpdate");
			try
			{
				stateRef = AccessTools.FieldRefAccess<StandardWorker, StandardWorker.State>("state");
				surpressRef = AccessTools.FieldRefAccess<StandardWorker, bool>("surpressForceSyncOnUpdate");
				waitingRef = AccessTools.FieldRefAccess<WearableAccessorizer, bool>("waitingForOutfitChangeFX");
			}
			catch (Exception e)
			{
				PerfLog.Warn("workersleep: fields not found (" + e.Message + "); off");
				return;
			}
			if (workerUpdate == null || wearUpdate == null || startWork == null || queueFx == null || onChanged == null
				|| lateUpdate == null)
			{
				PerfLog.Warn("workersleep: StandardWorker/WearableAccessorizer/SpeedControlScreen/Game are not the "
					+ "shape this lever was written against; off");
				return;
			}
			try
			{
				harmony.Patch(workerUpdate, prefix: new HarmonyMethod(typeof(WorkerSleep), nameof(WorkerUpdatePrefix)));
				harmony.Patch(wearUpdate, prefix: new HarmonyMethod(typeof(WorkerSleep), nameof(WearUpdatePrefix)));
				harmony.Patch(startWork, postfix: new HarmonyMethod(typeof(WorkerSleep), nameof(StartWorkPostfix)));
				harmony.Patch(queueFx, postfix: new HarmonyMethod(typeof(WorkerSleep), nameof(QueueFxPostfix)));
				harmony.Patch(onChanged, postfix: new HarmonyMethod(typeof(WorkerSleep), nameof(ScaleCheck)));
				harmony.Patch(lateUpdate, postfix: new HarmonyMethod(typeof(WorkerSleep), nameof(ScaleCheck)));
				installed = true;
			}
			catch (Exception e)
			{
				PerfLog.Warn("workersleep: could not patch: " + e.Message);
				return;
			}
			PerfLog.Info("workersleep: installed on StandardWorker.Update/StartWork, "
				+ "WearableAccessorizer.Update/QueueOutfitChangedFX, SpeedControlScreen.OnChanged, Game.LateUpdate");
		}

		private static bool WorkerUpdatePrefix(StandardWorker __instance)
		{
			if (!active)
			{
				return true;
			}
			bool working = stateRef(__instance) == StandardWorker.State.Working && !surpressRef(__instance);
			if (working && Time.timeScale > 0f)
			{
				return true;
			}
			__instance.enabled = false;
			asleep.Add(__instance);
			if (working)
			{
				pausedWorkers.Add(__instance);
			}
			workerSlept++;
			return false;
		}

		private static bool WearUpdatePrefix(WearableAccessorizer __instance)
		{
			if (!active || waitingRef(__instance))
			{
				return true;
			}
			__instance.enabled = false;
			asleep.Add(__instance);
			wearSlept++;
			return false;
		}

		private static void StartWorkPostfix(StandardWorker __instance)
		{
			Wake(__instance);
		}

		private static void QueueFxPostfix(WearableAccessorizer __instance)
		{
			Wake(__instance);
		}

		private static void Wake(Behaviour b)
		{
			if (active && !b.enabled)
			{
				b.enabled = true;
				woken++;
			}
		}

		private static void ScaleCheck()
		{
			if (pausedWorkers.Count == 0 || Time.timeScale == 0f)
			{
				return;
			}
			foreach (StandardWorker worker in pausedWorkers)
			{
				if (worker != null && !worker.enabled)
				{
					worker.enabled = true;
					unpauseWoken++;
				}
			}
			pausedWorkers.Clear();
		}

		public static void WakeAll()
		{
			foreach (Behaviour b in asleep)
			{
				if (b != null && !b.enabled)
				{
					b.enabled = true;
				}
			}
			asleep.Clear();
			pausedWorkers.Clear();
		}

		public static void Reset()
		{
			workerSlept = wearSlept = woken = unpauseWoken = 0;
		}

		public static void LogSummary(string what)
		{
			PerfLog.Info("workersleep: " + what + " -- " + workerSlept + " workers and " + wearSlept
				+ " accessorizers put to sleep, " + woken + " woken by StartWork/QueueOutfitChangedFX, "
				+ unpauseWoken + " by unpause, " + asleep.Count + " on the sleep list");
		}
	}
}
