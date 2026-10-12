using System;
using HarmonyLib;

namespace OniPerf
{
	/// <summary>
	/// An anim controller on an inactive GameObject leaves the anim updater's list instead of being
	/// called every frame for nothing.
	///
	/// **The mechanism, measured.** Every dupe carries 14 laser-effect children
	/// (`BaseMinionConfig.SetupLaserEffects`), each with its own `KBatchedAnimController`, switched
	/// by `KBatchedAnimEventToggler` with `SetActive`. Once an effect has been active, its controller
	/// has a movement-state handler on `CellChangeMonitor`, and `ConfigureUpdateListener` registers
	/// it for per-frame updates whenever it is "moving", active or not. A deactivated child of a
	/// walking dupe moves with it, so the pickup and store beams of every hauling dupe sit on
	/// `KBatchedAnimUpdater`'s list while switched off: about 273 of the 646 `UpdateAnim` calls a
	/// frame on the bench colony. Each of those calls does nothing. The controller has no batch
	/// (`Disable` deregistered it), so the moved branch is skipped, and `IsActive()` is false, so it
	/// returns before the frame advance.
	///
	/// **The lever.** A prefix on `UpdateAnim`: a controller that is not active and enabled and has
	/// no batch is taken off the update list (`UpdateUnregister`, the updater's own deferred removal)
	/// and the call is skipped. The call it replaces had no effect, so nothing observable changes.
	/// Reactivation goes through `Enable`, which re-registers through `ConfigureUpdateListener`.
	/// If a movement-state change re-adds a parked controller, the next call parks it again.
	/// **Every speed.**
	///
	/// Switched by the `parkAnims` key in config.json (on by default).
	/// </summary>
	internal static class ParkAnims
	{
		public static bool Active;

		private static AccessTools.FieldRef<KBatchedAnimController, KAnimBatch> batchRef;
		private static long parked;
		private static bool installed;

		public static void Install(Harmony harmony)
		{
			if (installed)
			{
				return;
			}
			var update = AccessTools.DeclaredMethod(typeof(KBatchedAnimController), "UpdateAnim");
			try
			{
				batchRef = AccessTools.FieldRefAccess<KBatchedAnimController, KAnimBatch>("batch");
			}
			catch (Exception e)
			{
				PerfLog.Warn("parkanims: batch field not found (" + e.Message + "); off");
				return;
			}
			if (update == null)
			{
				PerfLog.Warn("parkanims: KBatchedAnimController.UpdateAnim not found; off");
				return;
			}
			try
			{
				harmony.Patch(update, prefix: new HarmonyMethod(typeof(ParkAnims), nameof(Prefix)) { priority = Priority.First });
				installed = true;
			}
			catch (Exception e)
			{
				PerfLog.Warn("parkanims: could not patch: " + e.Message);
				return;
			}
			PerfLog.Info("parkanims: installed on KBatchedAnimController.UpdateAnim");
		}

		private static bool Prefix(KBatchedAnimController __instance)
		{
			if (!Active || __instance.isActiveAndEnabled || batchRef(__instance) != null)
			{
				return true;
			}
			KBatchedAnimUpdater updater = Singleton<KBatchedAnimUpdater>.Instance;
			if (updater == null)
			{
				return true;
			}
			updater.UpdateUnregister(__instance);
			parked++;
			return false;
		}

		public static void Reset()
		{
			parked = 0;
		}

		public static void LogSummary(string what)
		{
			PerfLog.Info("parkanims: " + what + " -- " + parked + " inactive controllers taken off the update list");
		}
	}
}
