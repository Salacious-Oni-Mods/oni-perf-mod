using System;
using HarmonyLib;
using UnityEngine;

namespace OniPerf
{
	/// <summary>
	/// The active tool's hover pass, repeated only when something it reads has moved.
	///
	/// **The mechanism, measured.** Every rendered frame, at every speed,
	/// `InterfaceTool.LateUpdate` on the active tool (SelectTool, normally) collects the
	/// selectables under the cursor, picks the one to hover and rebuilds the hover card through
	/// `SelectToolHoverTextCard.UpdateHoverElements`: 0.165 ms of a 3.6 ms paused frame, 0.11 of it
	/// the card. The card is drawn with pooled widgets (`HoverTextDrawer`) that stay as they were
	/// until the next `BeginDrawing`/`EndDrawing` pair, so a frame that skips the pass keeps
	/// showing the previous card.
	///
	/// **The lever.** A prefix on `InterfaceTool.LateUpdate` skips the pass when all of these are
	/// the same as at the last pass that ran: the tool, the cursor position, the main camera's
	/// position and zoom, the tool's `hasFocus`, the overlay mode and the selected object; no
	/// mouse button is down, pressed or released; no gamepad is the active controller; and that
	/// pass is younger than <see cref="MaxAgeS"/> of real time. What can go stale while the mouse
	/// is still is what changes under a still cursor: a dupe walking under it, a value on the card
	/// ticking. Both follow within <see cref="MaxAgeS"/>. The hover sound and the hover-changed
	/// event fire when the hovered object changes, so they can be at most that late too. **A
	/// saving only while the mouse is still**, at any speed; with the mouse moving the pass runs
	/// every frame as before. Tools that override `LateUpdate` without calling the base (the brush
	/// tools, the starmap select tool) are not affected.
	///
	/// Switched by the `hoverCache` key in config.json (on by default).
	/// </summary>
	internal static class HoverCache
	{
		public static bool Active;

		public const float MaxAgeS = 0.1f;

		private static InterfaceTool lastTool;
		private static Vector3 lastMouse, lastCamPos;
		private static float lastZoom, lastAt = float.NegativeInfinity;
		private static bool lastFocus;
		private static HashedString lastMode;
		private static KSelectable lastSelected;
		private static long ran, skipped;
		private static bool installed;

		public static void Install(Harmony harmony)
		{
			if (installed)
			{
				return;
			}
			var target = AccessTools.DeclaredMethod(typeof(InterfaceTool), "LateUpdate");
			if (target == null)
			{
				PerfLog.Warn("hovercache: no InterfaceTool.LateUpdate; off");
				return;
			}
			try
			{
				harmony.Patch(target, prefix: new HarmonyMethod(typeof(HoverCache), nameof(Prefix)));
				installed = true;
			}
			catch (Exception e)
			{
				PerfLog.Warn("hovercache: could not patch: " + e.Message);
				return;
			}
			PerfLog.Info("hovercache: installed on InterfaceTool.LateUpdate");
		}

		private static bool Prefix(InterfaceTool __instance)
		{
			if (!Active)
			{
				ran++;
				return true;
			}
			Camera cam = Camera.main;
			Vector3 mouse = KInputManager.GetMousePos();
			Vector3 camPos = cam != null ? cam.transform.position : Vector3.zero;
			float zoom = cam != null ? cam.orthographicSize : 0f;
			HashedString mode = OverlayScreen.Instance != null ? OverlayScreen.Instance.mode : HashedString.Invalid;
			KSelectable selected = SelectTool.Instance != null ? SelectTool.Instance.selected : null;
			float now = Time.realtimeSinceStartup;
			bool still = __instance == lastTool && mouse == lastMouse && camPos == lastCamPos && zoom == lastZoom
				&& __instance.hasFocus == lastFocus && mode == lastMode && selected == lastSelected
				&& !KInputManager.currentControllerIsGamepad && !AnyButton() && now - lastAt < MaxAgeS;
			if (still)
			{
				skipped++;
				return false;
			}
			lastTool = __instance;
			lastMouse = mouse;
			lastCamPos = camPos;
			lastZoom = zoom;
			lastFocus = __instance.hasFocus;
			lastMode = mode;
			lastSelected = selected;
			lastAt = AnyButton() ? float.NegativeInfinity : now;
			ran++;
			return true;
		}

		private static bool AnyButton()
		{
			for (int b = 0; b < 3; b++)
			{
				if (Input.GetMouseButton(b) || Input.GetMouseButtonDown(b) || Input.GetMouseButtonUp(b))
				{
					return true;
				}
			}
			return false;
		}

		public static void Reset()
		{
			ran = skipped = 0;
			lastAt = float.NegativeInfinity;
		}

		public static void LogSummary(string what)
		{
			PerfLog.Info("hovercache: " + what + " -- " + ran + " ran, " + skipped + " skipped");
		}
	}
}
