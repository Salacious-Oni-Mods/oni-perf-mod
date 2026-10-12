using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace OniPerf
{
	/// <summary>
	/// Components whose per-frame callback has nothing to do are disabled until the event that gives
	/// them work, so Unity stops dispatching to them.
	///
	/// **The mechanism.** With five levers on, 2,444 ticking components cost 0.93 ms of a 6.2 ms
	/// paused frame, and a measurement showed the cost is Unity's dispatch, not their
	/// bodies: only 44 of 421 `KBatchedAnimTracker`s ever get past their first `if`. A component
	/// Unity calls every frame costs roughly half a microsecond whether or not it does anything, and
	/// Harmony cannot take a method out of Unity's dispatch -- only `enabled = false` can.
	///
	/// **Two types, each with an exact wake event in the game code:**
	///
	///   - `KBatchedAnimTracker.LateUpdate` does nothing unless its controller `IsVisible()` (or
	///     `forceAlwaysVisible`, or a pending `forceUpdate`). A controller's `isVisible` becomes true
	///     only in its constructor and in `KBatchedAnimController.SetVisiblity(true)`, so a postfix
	///     there wakes every tracker slept under that controller.
	///   - `KBatchedAnimHeatPostProcessingEffect.Update` plays a sound once a second, and only when
	///     the overlay is Temperature. Its `enabled` also gates its visual parameter, but that
	///     parameter is read only by the post-process `OverlayModes.Temperature` switches on
	///     (`CameraController.EnableKAnimPostProcessingEffect`), and waking it runs `OnCmpEnable` ->
	///     `RefreshEffectVisualState`, which restores the parameter. A postfix on
	///     `OverlayScreen.ToggleOverlay` wakes them all on entering Temperature.
	///
	/// **Only what this lever put to sleep is ever woken.** Four game methods set `enabled` on these
	/// types themselves (`AirConditioner.OnActiveChanged`, `SeekAndInstallBionicUpgradeChore.
	/// SetOverrideAnimSymbol`, `BionicBedTimeMonitor`'s `EnableLight`/`DisableLight`). Their
	/// `Behaviour.set_enabled` calls are rerouted through `SetEnabled`, which drops the component
	/// from the slept set first, so a component the game switched off is never switched back on by
	/// a wake. The tracker also re-enables itself in `OnAnimStart`; that needs no reroute, because a
	/// running tracker simply sleeps again if it is still idle.
	///
	/// **Not the paused-only saving .** Offscreen trackers and non-Temperature overlays are the
	/// normal state at any speed.
	///
	/// Switched by the `idleSleep` key in config.json (on by default).
	/// </summary>
	internal static class IdleSleep
	{
		public static bool Active;
		public static bool Trackers = true;
		public static bool Heat = true;

		private static readonly HashSet<Behaviour> slept = new HashSet<Behaviour>();
		private static readonly Dictionary<KBatchedAnimController, List<KBatchedAnimTracker>> byController =
			new Dictionary<KBatchedAnimController, List<KBatchedAnimTracker>>();
		private static readonly List<KBatchedAnimHeatPostProcessingEffect> sleptHeat =
			new List<KBatchedAnimHeatPostProcessingEffect>();
		private static AccessTools.FieldRef<KBatchedAnimTracker, bool> forceUpdateRef;
		private static long trackerSleeps, trackerWakes, heatSleeps, heatWakes, forgotten;
		private static bool installed;

		public static int AsleepCount => slept.Count;

		public static void Install(Harmony harmony)
		{
			if (installed)
			{
				return;
			}
			try
			{
				forceUpdateRef = AccessTools.FieldRefAccess<KBatchedAnimTracker, bool>("forceUpdate");
				Patch(harmony, typeof(KBatchedAnimTracker), "LateUpdate", postfix: nameof(TrackerPostfix));
				Patch(harmony, typeof(KBatchedAnimController), "SetVisiblity", postfix: nameof(VisibilityPostfix));
				Patch(harmony, typeof(KBatchedAnimHeatPostProcessingEffect), "Update", postfix: nameof(HeatPostfix));
				Patch(harmony, typeof(OverlayScreen), "ToggleOverlay", postfix: nameof(OverlayPostfix));
				Patch(harmony, typeof(AirConditioner), "OnActiveChanged", transpiler: nameof(Reroute));
				Patch(harmony, typeof(SeekAndInstallBionicUpgradeChore), "SetOverrideAnimSymbol", transpiler: nameof(Reroute));
				int bed = 0;
				// The nested Instance's pair writes the tracker's enabled; the outer class has static
				// EnableLight/DisableLight(smi) wrappers that do not, and Reroute would refuse them.
				foreach (Type t in typeof(BionicBedTimeMonitor).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
				{
					foreach (string name in new[] { "EnableLight", "DisableLight" })
					{
						if (AccessTools.DeclaredMethod(t, name) != null)
						{
							Patch(harmony, t, name, transpiler: nameof(Reroute));
							bed++;
						}
					}
				}
				if (bed != 2)
				{
					throw new Exception("expected BionicBedTimeMonitor EnableLight + DisableLight, found " + bed);
				}
				installed = true;
			}
			catch (Exception e)
			{
				// A partial install is not safe: a sleep path without its wake path strands components.
				PerfLog.Warn("idlesleep: could not patch, lever stays off: " + e.Message);
				Active = false;
				return;
			}
			PerfLog.Info("idlesleep: installed (tracker + heat effect sleep, 4 enabled-setters rerouted)");
		}

		private static void Patch(Harmony harmony, Type type, string method, string postfix = null, string transpiler = null)
		{
			MethodInfo m = AccessTools.DeclaredMethod(type, method);
			if (m == null)
			{
				throw new Exception("no " + type.Name + "." + method);
			}
			harmony.Patch(m,
				postfix: postfix != null ? new HarmonyMethod(typeof(IdleSleep), postfix) : null,
				transpiler: transpiler != null ? new HarmonyMethod(typeof(IdleSleep), transpiler) : null);
		}

		/// <summary>
		/// Every `Behaviour.set_enabled` in the patched method goes through `SetEnabled`. Refuses a
		/// method with none, so a game update that moves the write shows up as an install failure.
		/// </summary>
		private static IEnumerable<CodeInstruction> Reroute(IEnumerable<CodeInstruction> instructions, MethodBase original)
		{
			MethodInfo setter = AccessTools.PropertySetter(typeof(Behaviour), nameof(Behaviour.enabled));
			MethodInfo helper = AccessTools.Method(typeof(IdleSleep), nameof(SetEnabled));
			var list = new List<CodeInstruction>(instructions);
			int n = 0;
			foreach (CodeInstruction ci in list)
			{
				if ((ci.opcode == OpCodes.Callvirt || ci.opcode == OpCodes.Call) && ci.operand is MethodInfo mi && mi == setter)
				{
					ci.opcode = OpCodes.Call;
					ci.operand = helper;
					n++;
				}
			}
			if (n == 0)
			{
				throw new Exception(original.DeclaringType.Name + "." + original.Name + " has no Behaviour.enabled write");
			}
			return list;
		}

		public static void SetEnabled(Behaviour b, bool value)
		{
			if (b != null && slept.Remove(b))
			{
				forgotten++;
			}
			b.enabled = value;
		}

		private static void TrackerPostfix(KBatchedAnimTracker __instance)
		{
			if (!Active || !Trackers || !__instance.enabled)
			{
				return;
			}
			KBatchedAnimController controller = __instance.controller;
			if (controller == null || controller.IsVisible() || __instance.forceAlwaysVisible
				|| forceUpdateRef(__instance))
			{
				return;
			}
			if (!byController.TryGetValue(controller, out List<KBatchedAnimTracker> list))
			{
				list = new List<KBatchedAnimTracker>(1);
				byController[controller] = list;
			}
			if (!list.Contains(__instance))
			{
				list.Add(__instance);
			}
			slept.Add(__instance);
			__instance.enabled = false;
			trackerSleeps++;
		}

		private static void VisibilityPostfix(KBatchedAnimController __instance, bool is_visible)
		{
			if (!is_visible || !byController.TryGetValue(__instance, out List<KBatchedAnimTracker> list))
			{
				return;
			}
			byController.Remove(__instance);
			foreach (KBatchedAnimTracker t in list)
			{
				if (t != null && slept.Remove(t))
				{
					t.enabled = true;
					trackerWakes++;
				}
			}
		}

		private static OverlayScreen overlayReady;
		private static int overlayNotReadyFrame = -1;

		/// <summary>
		/// Whether the overlay is Temperature; false with `known` false while that cannot be told.
		/// `OverlayScreen.mode` throws a NullReferenceException for the first frame(s) after a load,
		/// before its current mode is set, and the first armed-at-load run logged 680 of them (one
		/// per heat effect). Until one read has succeeded on this OverlayScreen, at most one attempt
		/// is made per frame, and an effect that cannot tell stays awake.
		/// </summary>
		private static bool TemperatureOverlay(out bool known)
		{
			known = false;
			OverlayScreen overlay = OverlayScreen.Instance;
			if (overlay == null)
			{
				return false;
			}
			if (!ReferenceEquals(overlay, overlayReady))
			{
				if (overlayNotReadyFrame == Time.frameCount)
				{
					return false;
				}
				try
				{
					HashedString unused = overlay.mode;
					overlayReady = overlay;
				}
				catch (NullReferenceException)
				{
					overlayNotReadyFrame = Time.frameCount;
					return false;
				}
			}
			known = true;
			return overlay.mode == OverlayModes.Temperature.ID;
		}

		private static void HeatPostfix(KBatchedAnimHeatPostProcessingEffect __instance)
		{
			if (!Active || !Heat || !__instance.enabled || TemperatureOverlay(out bool known) || !known)
			{
				return;
			}
			slept.Add(__instance);
			sleptHeat.Add(__instance);
			__instance.enabled = false;
			heatSleeps++;
		}

		private static void OverlayPostfix()
		{
			if (TemperatureOverlay(out bool _))
			{
				WakeHeat();
			}
		}

		private static void WakeHeat()
		{
			foreach (KBatchedAnimHeatPostProcessingEffect h in sleptHeat)
			{
				if (h != null && slept.Remove(h))
				{
					h.enabled = true;
					heatWakes++;
				}
			}
			sleptHeat.Clear();
		}

		/// <summary>Wakes everything this lever slept. Returns how many were woken.</summary>
		public static int WakeAll()
		{
			int n = 0;
			foreach (Behaviour b in slept)
			{
				if (b != null)
				{
					b.enabled = true;
					n++;
				}
			}
			slept.Clear();
			byController.Clear();
			sleptHeat.Clear();
			return n;
		}

		/// <summary>
		/// Components this lever holds asleep that should be awake: a tracker whose controller is
		/// visible (a missed wake), or a heat effect while the overlay is Temperature. Must be 0.
		/// </summary>
		public static int Stranded(out int trackers, out int heat)
		{
			trackers = heat = 0;
			bool temperature = TemperatureOverlay(out bool _);
			foreach (Behaviour b in slept)
			{
				if (b is KBatchedAnimTracker t && t != null && t.controller != null && t.controller.IsVisible())
				{
					trackers++;
				}
				else if (b is KBatchedAnimHeatPostProcessingEffect && b != null && temperature)
				{
					heat++;
				}
			}
			return trackers + heat;
		}

		public static void Reset()
		{
			trackerSleeps = trackerWakes = heatSleeps = heatWakes = forgotten = 0;
		}

		public static void LogSummary(string where)
		{
			int asleepTrackers = 0, asleepHeat = 0;
			foreach (Behaviour b in slept)
			{
				if (b is KBatchedAnimTracker)
				{
					asleepTrackers++;
				}
				else if (b is KBatchedAnimHeatPostProcessingEffect)
				{
					asleepHeat++;
				}
			}
			PerfLog.Info("idlesleep: " + where + ": asleep now " + asleepTrackers + " trackers, " + asleepHeat
				+ " heat effects; tracker sleeps " + trackerSleeps + " wakes " + trackerWakes + "; heat sleeps "
				+ heatSleeps + " wakes " + heatWakes + "; forgotten by game setters " + forgotten);
		}
	}
}
