using HarmonyLib;
using UnityEngine;

namespace OniPerf
{
	/// <summary>
	/// The colony stays paused while the defer-object drain builds it.
	///
	/// **Why.** Until the drain ends most of the colony is not there yet, and the sim's own frame
	/// (sim tick, brains, state-machine updaters: ~14-27 ms a frame on Euphoria)
	/// is what holds the drain to ~10-17 fps. Paused, that frame is cheap, so with
	/// FastLoadOptions.AsyncLoadDeferFps the drain takes most of each frame and finishes sooner at the
	/// same frame rate. And a paused drain ends in the colony Klei's own load makes (the building
	/// digest is byte-identical); an unpaused one runs the sim on a part-built colony -- machines
	/// off-screen not running, networks with gaps, no rooms -- and ends somewhere else.
	///
	/// **How.** Klei's pause, which stacks (`SpeedControlScreen.pauseCount`). A game that loads
	/// running gets one Pause when the drain begins, and the drain's end takes it back, so the
	/// colony runs as it loaded; a game that loads paused is left paused.
	///
	/// **No unpausing during the drain**: a live run with an "Unpause anyway" button showed how slow
	/// and lossy the unpaused drain is. An Unpause that would take the count to zero is held back: every way to unpause goes
	/// through SpeedControlScreen.Unpause (space, the pause button, Klei's sequences; SetSpeed does
	/// not unpause). Instead a Klei ConfirmDialogScreen says the colony is still loading, that the
	/// game stays paused until it has, and where its progress shows; OK or Escape close it. Unpauses
	/// that only lower a stacked count (a modal screen closing over the paused game) pass through.
	/// The dialog closes itself when the drain ends.
	///
	/// Off unless the keep-paused drain.
	/// </summary>
	internal static class DrainPause
	{
		private static bool installed, held, releasing;
		private static ConfirmDialogScreen dialog;
		private static int dialogsShown;
		private static AccessTools.FieldRef<SpeedControlScreen, int> pauseCountRef;

		/// <summary>True while the drain's own pause is in effect.</summary>
		public static bool Held => held;

		public static void Install(Harmony harmony)
		{
			if (installed)
			{
				return;
			}
			try
			{
				pauseCountRef = AccessTools.FieldRefAccess<SpeedControlScreen, int>("pauseCount");
				harmony.Patch(AccessTools.DeclaredMethod(typeof(SpeedControlScreen), nameof(SpeedControlScreen.Unpause)),
					prefix: new HarmonyMethod(typeof(DrainPause), nameof(UnpausePrefix)));
				installed = true;
				PerfLog.Info("drain-pause: installed; the colony starts paused while the drain builds it");
			}
			catch (System.Exception e)
			{
				PerfLog.Warn("drain-pause: could not patch: " + e.Message);
			}
		}

		/// <summary>When the drain begins.</summary>
		public static void Hold()
		{
			SpeedControlScreen s = SpeedControlScreen.Instance;
			if (!installed || held || s == null)
			{
				return;
			}
			if (s.IsPaused)
			{
				PerfLog.Info("drain-pause: the game loaded paused; left as it is");
				return;
			}
			s.Pause(false);
			held = true;
			PerfLog.Info("drain-pause: paused for the drain (the game loaded running)");
		}

		/// <summary>On every way out of the drain.</summary>
		public static void Release()
		{
			CloseDialog();
			if (dialogsShown > 0)
			{
				PerfLog.Info("drain-pause: still-loading dialog shown " + dialogsShown + " times");
				dialogsShown = 0;
			}
			if (!held)
			{
				return;
			}
			held = false;
			SpeedControlScreen s = SpeedControlScreen.Instance;
			if (s != null)
			{
				releasing = true;
				try
				{
					s.Unpause(false);
				}
				finally
				{
					releasing = false;
				}
			}
			PerfLog.Info("drain-pause: drain over, its pause taken back; paused now " + (s != null && s.IsPaused));
		}

		private static bool UnpausePrefix(SpeedControlScreen __instance)
		{
			if (!DeferredObject.Draining || releasing || pauseCountRef(__instance) > 1)
			{
				return true;
			}
			ShowDialog();
			return false;
		}

		private static void ShowDialog()
		{
			if (dialog != null && dialog.gameObject.activeInHierarchy)
			{
				return;
			}
			GameObject parent = GameScreenManager.Instance != null ? GameScreenManager.Instance.ssOverlayCanvas : null;
			if (ScreenPrefabs.Instance == null || parent == null)
			{
				return;
			}
			try
			{
				dialog = Util.KInstantiateUI<ConfirmDialogScreen>(ScreenPrefabs.Instance.ConfirmDialogScreen.gameObject, parent, force_active: true);
				dialog.PopupConfirmDialog(
					"The colony is still loading. You can follow its progress in the notifications on the left.\n\n" +
					"The game stays paused until loading finishes.",
					on_confirm: () => { },
					on_cancel: null,
					title_text: "Still loading",
					confirm_text: "OK");
				dialogsShown++;
				PerfLog.Info("drain-pause: unpause held back during the drain; still-loading dialog shown");
			}
			catch (System.Exception e)
			{
				PerfLog.Warn("drain-pause: could not show the still-loading dialog: " + e.Message);
				dialog = null;
			}
		}

		private static void CloseDialog()
		{
			if (dialog != null)
			{
				if (dialog.gameObject.activeInHierarchy)
				{
					dialog.Deactivate();
				}
				dialog = null;
			}
		}
	}
}
