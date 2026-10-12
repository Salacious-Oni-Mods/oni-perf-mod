using System;
using System.Collections;
using HarmonyLib;
using UnityEngine;

namespace OniPerf
{
	/// <summary>
	/// Two per-frame passes over dupes that cannot change their answer while the game is paused
	/// and the camera is still, run at most every <see cref="MaxAgeS"/> in that state.
	///
	/// **The mechanism, measured.** In a 3.4 ms paused frame:
	///
	/// - `AnimEventHandlerManager.LateUpdate`, 0.11 ms: for every dupe in view, the anim collider's
	///   offset from the pivot symbol, the nav type's offset and the transform. With no sim time
	///   passing nothing moves and no anim advances, so every offset comes out the same.
	/// - `NameDisplayScreen.Culling`, 0.07 ms: up to 500 name entries a frame, each shown or
	///   hidden by whether its object's position is inside the visible cell range. With nothing
	///   moving, only a camera move or an entry added or removed can change that.
	///
	/// **The lever.** A prefix on each skips the call while all of these hold: `Time.timeScale` is
	/// 0 now and was at the last call that ran, the main camera's position and zoom are the ones
	/// that call saw, the handler or entry count is the same, and that call is younger than
	/// <see cref="MaxAgeS"/> of real time. The age bound covers anything that moves an object while
	/// paused (sandbox and debug tools): its collider and label catch up within it. At speed both
	/// run every frame as before. **Paused-only**, by construction: it saves nothing while the
	/// colony runs.
	///
	/// Switched by the `pausedSkip` key in config.json (on by default).
	/// </summary>
	internal static class PausedSkip
	{
		public static bool Anim, Names;

		public const float MaxAgeS = 0.25f;

		private sealed class Key
		{
			public bool Valid;
			public Vector3 CamPos;
			public float Zoom, At;
			public int Count;
			public long Ran, Skipped;
		}

		private static readonly Key animKey = new Key(), namesKey = new Key();
		private static AccessTools.FieldRef<AnimEventHandlerManager, IList> handlersRef;
		private static AccessTools.FieldRef<NameDisplayScreen, IList> entriesRef;
		private static bool installed;

		public static void Install(Harmony harmony)
		{
			if (installed)
			{
				return;
			}
			var anim = AccessTools.DeclaredMethod(typeof(AnimEventHandlerManager), "LateUpdate");
			var culling = AccessTools.DeclaredMethod(typeof(NameDisplayScreen), "Culling");
			try
			{
				handlersRef = AccessTools.FieldRefAccess<AnimEventHandlerManager, IList>("handlers");
				entriesRef = AccessTools.FieldRefAccess<NameDisplayScreen, IList>("entries");
			}
			catch (Exception e)
			{
				PerfLog.Warn("pausedskip: fields not found (" + e.Message + "); off");
				return;
			}
			if (anim == null || culling == null)
			{
				PerfLog.Warn("pausedskip: AnimEventHandlerManager.LateUpdate/NameDisplayScreen.Culling not found; off");
				return;
			}
			try
			{
				harmony.Patch(anim, prefix: new HarmonyMethod(typeof(PausedSkip), nameof(AnimPrefix)));
				harmony.Patch(culling, prefix: new HarmonyMethod(typeof(PausedSkip), nameof(NamesPrefix)));
				installed = true;
			}
			catch (Exception e)
			{
				PerfLog.Warn("pausedskip: could not patch: " + e.Message);
				return;
			}
			PerfLog.Info("pausedskip: installed on AnimEventHandlerManager.LateUpdate, NameDisplayScreen.Culling");
		}

		private static bool AnimPrefix(AnimEventHandlerManager __instance)
		{
			return Decide(Anim, animKey, handlersRef(__instance));
		}

		private static bool NamesPrefix(NameDisplayScreen __instance)
		{
			return Decide(Names, namesKey, entriesRef(__instance));
		}

		private static bool Decide(bool on, Key key, IList list)
		{
			bool paused = Time.timeScale == 0f;
			Camera cam = Camera.main;
			Vector3 camPos = cam != null ? cam.transform.position : Vector3.zero;
			float zoom = cam != null ? cam.orthographicSize : 0f;
			int count = list != null ? list.Count : -1;
			float now = Time.realtimeSinceStartup;
			if (on && paused && key.Valid && camPos == key.CamPos && zoom == key.Zoom && count == key.Count
				&& now - key.At < MaxAgeS)
			{
				key.Skipped++;
				return false;
			}
			// Only a call made while paused can stand in for later paused frames.
			key.Valid = paused;
			key.CamPos = camPos;
			key.Zoom = zoom;
			key.Count = count;
			key.At = now;
			key.Ran++;
			return true;
		}

		public static void Reset()
		{
			animKey.Valid = namesKey.Valid = false;
			animKey.Ran = animKey.Skipped = namesKey.Ran = namesKey.Skipped = 0;
		}

		public static void LogSummary(string what)
		{
			PerfLog.Info("pausedskip: " + what + " -- anim " + animKey.Ran + " ran, " + animKey.Skipped + " skipped; names "
				+ namesKey.Ran + " ran, " + namesKey.Skipped + " skipped");
		}
	}
}
