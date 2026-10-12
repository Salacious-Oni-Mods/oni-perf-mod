using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;

namespace OniPerf
{
	/// <summary>
	/// The UI raycast, repeated while nothing it depends on has moved.
	///
	/// (`EventSystem` is written in full: ONI's firstpass has a global `EventSystem` that wins over the
	/// `using`.)
	///
	/// **The mechanism, measured.** Once per rendered frame ONI's input module
	/// (`VirtualInputModule.GetMousePointerEventData`) calls `EventSystem.RaycastAll` at the cursor,
	/// and every enabled `GraphicRaycaster` tests every raycast-target graphic on its canvas: about
	/// 0.16 ms of a paused frame for the visible HUD after ScreenSleep's graphics half. With the
	/// mouse still and the UI still, the answer is the same list every frame.
	///
	/// **The lever.** A prefix on `EventSystem.RaycastAll` hands back the previous result list
	/// instead of raycasting when all of these hold: the pointer position is the one the list was
	/// made for, no mouse button is down, pressed or released this frame, the scroll delta is
	/// zero, the set of enabled raycasters is the same size, every hit object still exists, and
	/// the list is younger than <see cref="MaxAgeS"/> of real time. A click therefore always
	/// raycasts. What can go stale is the hover state when the UI under a still cursor changes (a
	/// panel opening under it): the hover follows within <see cref="MaxAgeS"/>. The other
	/// `RaycastAll` callers (tools on a click, the schedule painter, the jobs table) either raycast
	/// with a button down or at a position the key tells apart. **A saving only while the mouse is
	/// still**, at any speed; with the mouse moving it raycasts every frame as before.
	///
	/// Switched by the `raycastCache` key in config.json (on by default).
	/// </summary>
	internal static class RaycastCache
	{
		public static bool Active;

		public const float MaxAgeS = 0.1f;

		private static readonly List<RaycastResult> cached = new List<RaycastResult>();
		private static Vector2 cachedPos;
		private static int cachedRaycasters = -1, cachedW, cachedH;
		private static float cachedAt = float.NegativeInfinity;
		private static IList raycasters;
		private static long raycast, reused;
		private static bool installed;

		public static void Install(Harmony harmony)
		{
			if (installed)
			{
				return;
			}
			var target = AccessTools.DeclaredMethod(typeof(UnityEngine.EventSystems.EventSystem), "RaycastAll");
			Type manager = typeof(UnityEngine.EventSystems.EventSystem).Assembly.GetType("UnityEngine.EventSystems.RaycasterManager");
			var get = manager == null ? null : AccessTools.Method(manager, "GetRaycasters");
			if (target == null || get == null)
			{
				PerfLog.Warn("raycastcache: not found: " + (target == null ? "EventSystem.RaycastAll " : "")
					+ (manager == null ? "RaycasterManager " : "") + (manager != null && get == null ? "RaycasterManager.GetRaycasters" : "")
					+ "; off");
				return;
			}
			try
			{
				raycasters = (IList)get.Invoke(null, null);
				harmony.Patch(target, prefix: new HarmonyMethod(typeof(RaycastCache), nameof(Prefix)),
					postfix: new HarmonyMethod(typeof(RaycastCache), nameof(Postfix)));
				installed = true;
			}
			catch (Exception e)
			{
				PerfLog.Warn("raycastcache: could not patch: " + e.Message);
				return;
			}
			PerfLog.Info("raycastcache: installed on EventSystem.RaycastAll");
		}

		private static bool Prefix(PointerEventData eventData, List<RaycastResult> raycastResults, out bool __state)
		{
			__state = false;
			if (!Active || eventData == null || raycastResults == null)
			{
				return true;
			}
			bool still = eventData.position == cachedPos && Screen.width == cachedW && Screen.height == cachedH
				&& raycasters.Count == cachedRaycasters && eventData.scrollDelta == Vector2.zero && !AnyButton()
				&& !KInputManager.currentControllerIsGamepad
				&& Time.realtimeSinceStartup - cachedAt < MaxAgeS;
			if (still)
			{
				for (int i = 0; i < cached.Count; i++)
				{
					if (cached[i].gameObject == null)
					{
						still = false;
						break;
					}
				}
			}
			if (still)
			{
				raycastResults.Clear();
				raycastResults.AddRange(cached);
				reused++;
				return false;
			}
			__state = true;
			raycast++;
			return true;
		}

		private static void Postfix(PointerEventData eventData, List<RaycastResult> raycastResults, bool __state)
		{
			if (!__state)
			{
				return;
			}
			cached.Clear();
			cached.AddRange(raycastResults);
			cachedPos = eventData.position;
			cachedW = Screen.width;
			cachedH = Screen.height;
			cachedRaycasters = raycasters.Count;
			cachedAt = AnyButton() || eventData.scrollDelta != Vector2.zero ? float.NegativeInfinity : Time.realtimeSinceStartup;
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
			raycast = reused = 0;
			cachedAt = float.NegativeInfinity;
		}

		public static void LogSummary(string what)
		{
			PerfLog.Info("raycastcache: " + what + " -- " + raycast + " raycast, " + reused + " reused");
		}
	}
}
