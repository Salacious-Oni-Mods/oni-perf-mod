using System;
using HarmonyLib;
using UnityEngine;

namespace OniPerf
{
	/// <summary>
	/// The `timelapseCamera`, which renders every frame and whose output nothing uses, switched off.
	///
	/// **The mechanism.** `CameraController` creates it
	/// with `CloneCamera(overlayCamera, "timelapseCamera")` only to host the `Timelapser` component:
	/// a copy of the Overlay camera's settings, depth between SimOverlay and Overlay, clear flags
	/// Nothing, on a GameObject that is never parented to the camera rig and never moved. The
	/// timelapse capture itself does not use it: `Timelapser.RenderAndPrint` calls
	/// `CameraController.RenderForTimelapser`, which renders `baseCamera` and `overlayCamera` by hand
	/// into the timelapse texture. Nothing in the game enables, disables or reads this camera
	/// after creation. It costs a full cull and setup every frame at every speed (0.061 ms in
	/// one measured split, −0.21 frame in one A/B).
	///
	/// **The lever.** `Camera.enabled = false` on it. The GameObject stays active, so `Timelapser`'s
	/// `Update` and its end-of-frame coroutine run as before. Not a paused-only saving. The screen was
	/// compared against vanilla screenshots.
	///
	/// **Half 2: `SimOverlayCamera` only while an overlay is open.** It renders the `SimDebugView`
	/// layer into `_SimDebugViewTex` every frame. The only reader is `SimDebugViewCompositor`, and
	/// `SimDebugView.Update` switches that compositor, and the plane the camera would draw, on only
	/// when the overlay mode is not None. With no overlay the camera renders an empty layer into a
	/// texture nothing samples (0.099 ms in one measured split). The lever gives the camera the same flag in
	/// a postfix on `SimDebugView.Update`, which runs before any camera renders, so the first overlay
	/// frame is drawn by the camera as in vanilla. When it switches off it clears the texture, so a
	/// reopened overlay cannot composite a stale picture from the last one.
	///
	/// **Half 3, <see cref="Cursor"/>: `VirtualCursorCamera` only while the gamepad cursor can show.**
	/// `VirtualCursorOverlayFix` renders the controller cursor's canvas through this camera into a
	/// screen-resolution texture (2560x1440 here), and its coroutine sets `enabled = true` every
	/// frame, as does `VirtualInputModule.UpdateModule`. The texture is shown only through
	/// `screenSpaceOverlayImage`, which the coroutine enables only while
	/// `KInputManager.currentControllerIsGamepad` or Ctrl+Alt+C is held. With a mouse the camera
	/// renders a cursor nothing displays (0.057 ms in one measured split). The lever sets `enabled` in a
	/// postfix on `Game.LateUpdate`, after both re-enable sites and before any camera renders, to
	/// "gamepad, or Ctrl+Alt+C, or the overlay image is showing". When the gamepad takes over, the
	/// coroutine waits 0.1 s before showing the image; the camera renders in that wait, so the image
	/// never shows a stale texture.
	///
	/// Switched by the `idleCams` key in config.json (on by default); it switches on all three halves.
	/// </summary>
	internal static class IdleCams
	{
		private static bool active;
		private static Camera camera;
		private static long simOverlayOff;
		private static bool installed;

		/// <summary>Half 3: VirtualCursorCamera only while the gamepad cursor can show.</summary>
		public static bool Cursor;
		private static VirtualCursorOverlayFix cursorFix;
		private static long cursorOff;

		public static void Install(Harmony harmony)
		{
			if (installed)
			{
				return;
			}
			var update = AccessTools.DeclaredMethod(typeof(SimDebugView), "Update");
			if (update == null)
			{
				PerfLog.Warn("idlecams: no SimDebugView.Update; SimOverlayCamera half off");
				return;
			}
			try
			{
				harmony.Patch(update, postfix: new HarmonyMethod(typeof(IdleCams), nameof(SimDebugViewPostfix)));
				harmony.Patch(AccessTools.DeclaredMethod(typeof(Game), "LateUpdate"),
					postfix: new HarmonyMethod(typeof(IdleCams), nameof(GameLateUpdatePostfix)));
				installed = true;
			}
			catch (Exception e)
			{
				PerfLog.Warn("idlecams: could not patch SimDebugView.Update: " + e.Message);
				return;
			}
			PerfLog.Info("idlecams: installed on SimDebugView.Update");
		}

		private static void SimDebugViewPostfix(SimDebugView __instance)
		{
			CameraController controller = CameraController.Instance;
			Camera sim = controller == null ? null : controller.simOverlayCamera;
			if (sim == null)
			{
				return;
			}
			bool want = !active || __instance.GetMode() != OverlayModes.None.ID;
			if (sim.enabled == want)
			{
				if (!want)
				{
					simOverlayOff++;
				}
				return;
			}
			sim.enabled = want;
			if (!want)
			{
				var target = sim.GetComponent<CameraRenderTexture>();
				RenderTexture rt = target == null ? null : target.GetTexture();
				if (rt != null)
				{
					RenderTexture previous = RenderTexture.active;
					RenderTexture.active = rt;
					GL.Clear(true, true, sim.backgroundColor);
					RenderTexture.active = previous;
				}
			}
		}

		private static void GameLateUpdatePostfix()
		{
			if (!Cursor)
			{
				return;
			}
			if (cursorFix == null)
			{
				cursorFix = UnityEngine.Object.FindAnyObjectByType<VirtualCursorOverlayFix>();
				if (cursorFix == null)
				{
					return;
				}
			}
			Camera cam = cursorFix.screenSpaceCamera;
			if (cam == null)
			{
				return;
			}
			bool want = KInputManager.currentControllerIsGamepad
				|| (Input.GetKey(KeyCode.LeftControl) && Input.GetKey(KeyCode.LeftAlt) && Input.GetKey(KeyCode.C))
				|| (cursorFix.screenSpaceOverlayImage != null && cursorFix.screenSpaceOverlayImage.enabled);
			if (cam.enabled != want)
			{
				cam.enabled = want;
			}
			if (!want)
			{
				cursorOff++;
			}
		}

		public static bool Active
		{
			get { return active; }
			set
			{
				active = value;
				Apply();
			}
		}

		/// <summary>Finds the camera once the game exists; safe to call every probe.</summary>
		public static void Apply()
		{
			if (camera == null && Game.Instance != null && Game.Instance.timelapser != null)
			{
				camera = Game.Instance.timelapser.GetComponent<Camera>();
			}
			if (camera == null)
			{
				return;
			}
			camera.enabled = !active;
		}

		public static void LogSummary(string what)
		{
			PerfLog.Info("idlecams: " + what + " -- timelapseCamera " + (camera == null ? "not found" : camera.enabled ? "rendering" : "off")
				+ "; SimOverlayCamera off for " + simOverlayOff + " frames; VirtualCursorCamera off for " + cursorOff + " frames");
			simOverlayOff = cursorOff = 0;
		}
	}
}
