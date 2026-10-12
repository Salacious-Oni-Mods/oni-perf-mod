using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace OniPerf
{
	/// <summary>
	/// World progress bars stop rewriting their fill when the change cannot be seen.
	///
	/// **The mechanism (read in code, not yet measured).** Every work progress
	/// bar (`Workable.ShowProgressBar`: digging, building, research, fabricating), every building HP
	/// bar and every fabricator bar is a `ProgressBar` parented to the world-space canvas.
	/// `ProgressBar.Update` runs every frame, calls its percent-complete function and writes the
	/// result to `Image.fillAmount`. While work is in progress the value changes every frame, so the
	/// bar's `Image` is re-queued for a graphic rebuild every frame and its canvas re-batches.
	/// `ProgressBar` does have `OnBecameInvisible`/`OnBecameVisible` handlers that disable it, but
	/// Unity sends those messages only to objects with a `Renderer`, and a UI object has none, so
	/// bars off screen keep updating too.
	///
	/// **The lever.** A prefix on `ProgressBar.Update` (which `HealthBar.Update` calls as its base)
	/// that:
	///
	///   - skips the bar entirely while its position is outside the visible cell range plus a
	///     two-cell margin. The fill is brought up to date on the first frame it is back in range.
	///   - on screen, calls the percent function as before but writes the fill only when it moved by
	///     at least one screen pixel of the bar's width, or reached 0 or 1. A bar is therefore at
	///     most one pixel behind, and never behind at the start or end.
	///
	/// Everything else in `ProgressBar` and `HealthBar` (visibility, timers, hiding) is untouched.
	/// **Every speed.**
	///
	/// Switched by the `barRate` key in config.json (on by default).
	/// </summary>
	internal static class BarRate
	{
		public static bool Active;

		private static AccessTools.FieldRef<ProgressBar, Func<float>> funcRef;
		private static readonly Vector3[] corners = new Vector3[4];
		private static int rangeFrame = -1;
		private static Vector2I rangeMin, rangeMax;
		private static float pixelsPerUnit;
		private static long calls, offscreen, subpixel, written;
		private static bool installed;

		public static void Install(Harmony harmony)
		{
			if (installed)
			{
				return;
			}
			var update = AccessTools.DeclaredMethod(typeof(ProgressBar), "Update");
			try
			{
				funcRef = AccessTools.FieldRefAccess<ProgressBar, Func<float>>("updatePercentFull");
			}
			catch (Exception e)
			{
				PerfLog.Warn("barrate: ProgressBar.updatePercentFull not found (" + e.Message + "); off");
				return;
			}
			if (update == null)
			{
				PerfLog.Warn("barrate: ProgressBar.Update not found; off");
				return;
			}
			try
			{
				harmony.Patch(update, prefix: new HarmonyMethod(typeof(BarRate), nameof(Prefix)) { priority = Priority.First });
				installed = true;
			}
			catch (Exception e)
			{
				PerfLog.Warn("barrate: could not patch: " + e.Message);
				return;
			}
			PerfLog.Info("barrate: installed on ProgressBar.Update");
		}

		/// <summary>
		/// The visible cell range and the camera's pixels per world unit, read once a frame. The
		/// same range `NameDisplayScreen` culls its labels against.
		/// </summary>
		private static bool Refresh()
		{
			if (rangeFrame == Time.frameCount)
			{
				return pixelsPerUnit > 0f;
			}
			rangeFrame = Time.frameCount;
			pixelsPerUnit = 0f;
			Camera cam = Game.MainCamera;
			if (cam == null || !cam.orthographic || cam.orthographicSize <= 0f)
			{
				return false;
			}
			Grid.GetVisibleCellRangeInActiveWorld(out rangeMin, out rangeMax);
			pixelsPerUnit = cam.pixelHeight / (2f * cam.orthographicSize);
			return pixelsPerUnit > 0f;
		}

		private static bool Prefix(ProgressBar __instance)
		{
			if (!Active)
			{
				return true;
			}
			Func<float> func = funcRef(__instance);
			if (func == null || func.Target.IsNullOrDestroyed() || __instance.bar == null || !Refresh())
			{
				return true;
			}
			calls++;
			Vector3 pos = __instance.transform.position;
			if (pos.x < rangeMin.x - 2 || pos.y < rangeMin.y - 2 || pos.x >= rangeMax.x + 2 || pos.y >= rangeMax.y + 2)
			{
				offscreen++;
				return false;
			}
			Image bar = __instance.bar;
			float value = Mathf.Clamp01(func());
			float current = bar.fillAmount;
			if (value == current)
			{
				subpixel++;
				return false;
			}
			if (value > 0f && value < 1f)
			{
				bar.rectTransform.GetWorldCorners(corners);
				float barPixels = (corners[2].x - corners[0].x) * pixelsPerUnit;
				if (Mathf.Abs(value - current) * barPixels < 1f)
				{
					subpixel++;
					return false;
				}
			}
			bar.fillAmount = value;
			written++;
			return false;
		}

		public static void Reset()
		{
			calls = offscreen = subpixel = written = 0;
		}

		public static void LogSummary(string what)
		{
			PerfLog.Info("barrate: " + what + " -- " + calls + " bar updates: " + offscreen + " off screen, "
				+ subpixel + " under a pixel, " + written + " written");
		}
	}
}
