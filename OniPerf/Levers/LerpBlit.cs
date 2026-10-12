using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace OniPerf
{
	/// <summary>
	/// The six `TextureLerper` blends drawn with one `Graphics.Blit` each, instead of by six Unity
	/// cameras. The first lever taken under the rewrite permission.
	///
	/// **The mechanism.** Every lerper `PropertyTextures` creates (6 of its 17 properties have
	/// `blend` set) builds, in `TextureLerper.Init`, a whole `Camera` -- orthographic, size 0.5,
	/// depth -100, culling mask `RTT` -- and a 4-vertex quad in the `RTT` layer for it to look at.
	/// Each camera sits at its own x offset so its frustum holds only its own quad. All that
	/// machinery draws one full-target quad through `Klei/LerpEffect`, which outputs
	/// `lerp(_SourceTex, _TargetTex, _Lerp)`. Unity pays per camera for the camera: culling, per-camera
	/// setup and state, before the one draw. Measured, Klei put the six at 0.52 ms of a 1.42 ms
	/// `FinishFrameRendering`, and the grouped `nocam` probe took the frame 11.63 → 11.11 ms and cull+draw 1.40 → 0.98.
	///
	/// **Why a blit is the same draw.** `LerpEffect`'s vertex shader maps the quad's object-space
	/// (0..1) through `_MeshParams` and `_WorldSizeInfo` into both position and UV. Klei's `Update`
	/// overwrites the visible-range result with `(0, 0, W, H)`, and `_WorldSizeInfo.zw` is `1/W, 1/H`,
	/// so the map is the identity: position = UV = the vertex. The camera's frustum is exactly the
	/// quad (quad at `offset..offset+1`, camera centred on it with half-height 0.5 and aspect 1), so
	/// the camera draws UV 0..1 over the whole target. `Graphics.Blit` draws a 0..1 quad over the
	/// whole target with an identity model matrix: the same vertices, the same UVs, the same pass
	/// (`ZTest Always`, no blend, `clearFlags = Nothing` on the camera so nothing clears first).
	///
	/// **When it draws.** The camera drew at depth -100, first of the frame's cameras. The blit draws
	/// inside `TextureLerper.Update`, which `PropertyTextures.LateUpdate` calls after it has set
	/// `_WorldSizeInfo` and after every property upload of the frame. Earlier than the camera, but
	/// after everything the draw reads and before anything samples the result.
	///
	/// **The argument is checked, not trusted.** The first time each lerper is taken over, the old
	/// path and the new one are both drawn into scratch float targets and read back. Any pixel that
	/// differs by more than <see cref="Tolerance"/> turns the lever off for the session and says
	/// which lerper and by how much. A flipped Y, a wrong matrix or a half-pixel offset all show up
	/// there, and none of them is a silent failure.
	///
	/// Switched by the `lerpBlit` key in config.json (on by default).
	/// <see cref="Disarm"/> gives the cameras back.
	/// </summary>
	internal static class LerpBlit
	{
		public static bool Active;

		private const float Tolerance = 1e-4f;

		private sealed class Taken
		{
			public Camera Camera;
			public MeshRenderer Renderer;
		}

		private static readonly Dictionary<TextureLerper, Taken> taken = new Dictionary<TextureLerper, Taken>();
		private static bool installed, failed;
		private static long blits;

		public static void Install(Harmony harmony)
		{
			if (installed)
			{
				return;
			}
			var update = AccessTools.DeclaredMethod(typeof(TextureLerper), "Update");
			if (update == null)
			{
				PerfLog.Warn("lerpblit: no TextureLerper.Update; off");
				return;
			}
			try
			{
				harmony.Patch(update, postfix: new HarmonyMethod(typeof(LerpBlit), nameof(UpdatePostfix)));
				installed = true;
			}
			catch (Exception e)
			{
				PerfLog.Warn("lerpblit: could not patch: " + e.Message);
				return;
			}
			PerfLog.Info("lerpblit: installed on TextureLerper.Update");
		}

		/// <summary>
		/// Runs after Klei's `Update` has set `_Lerp`, `_SourceTex` and `_MeshParams` and swapped the
		/// ping-pong targets, which is exactly the state the camera would have drawn with.
		/// </summary>
		private static void UpdatePostfix(TextureLerper __instance, Material ___Material, RenderTexture ___source,
			RenderTexture ___dest, Camera ___textureCam, GameObject ___meshGO)
		{
			if (!Active || failed || ___Material == null || ___dest == null)
			{
				return;
			}
			if (!taken.ContainsKey(__instance))
			{
				if (!TakeOver(__instance, ___Material, ___source, ___dest, ___textureCam, ___meshGO))
				{
					return;
				}
			}
			RenderTexture was = RenderTexture.active;
			Graphics.Blit(___source, ___dest, ___Material, 0);
			RenderTexture.active = was;
			blits++;
		}

		private static bool TakeOver(TextureLerper lerper, Material material, RenderTexture source,
			RenderTexture dest, Camera camera, GameObject meshGO)
		{
			if (camera == null || meshGO == null)
			{
				PerfLog.Warn("lerpblit: " + lerper.name + " has no camera or quad; lever off");
				Fail();
				return false;
			}
			float diff, contrast;
			int pixels;
			if (!SelfCheck(material, source, dest, camera, out diff, out pixels, out contrast))
			{
				Fail();
				return false;
			}
			if (diff > Tolerance)
			{
				PerfLog.Warn("lerpblit: self-check " + lerper.name + " FAILED, max |diff| " + diff.ToString("G4")
					+ " over " + pixels + " px (tolerance " + Tolerance + "); lever off, cameras restored");
				Fail();
				return false;
			}
			PerfLog.Info("lerpblit: self-check " + lerper.name + " max |diff| " + diff.ToString("G4") + " over "
				+ pixels + " px, image spread " + contrast.ToString("G4")
				+ (contrast <= Tolerance ? " (UNIFORM: this check proves nothing about geometry)" : "")
				+ "; camera '" + camera.name + "' retired");
			var t = new Taken { Camera = camera, Renderer = meshGO.GetComponent<MeshRenderer>() };
			camera.enabled = false;
			if (t.Renderer != null)
			{
				t.Renderer.enabled = false;
			}
			taken[lerper] = t;
			return true;
		}

		/// <summary>
		/// Draws the camera path and the blit path into two scratch float targets of the lerper's
		/// size, from the same material state, and returns the largest channel difference. Float
		/// targets so an 8-bit lerper's rounding cannot hide a sub-texel offset.
		/// </summary>
		private static bool SelfCheck(Material material, RenderTexture source, RenderTexture dest, Camera camera,
			out float maxDiff, out int pixels, out float contrast)
		{
			maxDiff = 0f;
			pixels = 0;
			contrast = 0f;
			int w = dest.width, h = dest.height;
			RenderTexture was = RenderTexture.active;
			RenderTexture camTarget = camera.targetTexture;
			var a = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBFloat);
			var b = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBFloat);
			var read = new Texture2D(w, h, TextureFormat.RGBAFloat, false);
			try
			{
				camera.targetTexture = a;
				camera.Render();
				Graphics.Blit(source, b, material, 0);

				RenderTexture.active = a;
				read.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
				Color[] pa = read.GetPixels();
				RenderTexture.active = b;
				read.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
				Color[] pb = read.GetPixels();
				pixels = pa.Length;
				float lo = float.MaxValue, hi = float.MinValue;
				for (int i = 0; i < pa.Length; i++)
				{
					float v = pa[i].r + pa[i].g + pa[i].b + pa[i].a;
					lo = Mathf.Min(lo, v);
					hi = Mathf.Max(hi, v);
					Color d = pa[i] - pb[i];
					float m = Mathf.Max(Mathf.Max(Mathf.Abs(d.r), Mathf.Abs(d.g)), Mathf.Max(Mathf.Abs(d.b), Mathf.Abs(d.a)));
					if (m > maxDiff)
					{
						maxDiff = m;
					}
				}
				// A uniform image passes any orientation or offset check, so a pass on one proves
				// nothing about the geometry. Report the spread with the verdict.
				contrast = pa.Length > 0 ? hi - lo : 0f;
				return true;
			}
			catch (Exception e)
			{
				PerfLog.Warn("lerpblit: self-check could not run: " + e.Message + "; lever off");
				return false;
			}
			finally
			{
				camera.targetTexture = camTarget;
				RenderTexture.active = was;
				a.Release();
				UnityEngine.Object.Destroy(a);
				b.Release();
				UnityEngine.Object.Destroy(b);
				UnityEngine.Object.Destroy(read);
			}
		}

		private static void Fail()
		{
			failed = true;
			Active = false;
			Disarm();
		}

		/// <summary>
		/// Gives every retired camera and quad back, so the next frame draws the way Klei's does.
		/// The next `Update` still points each camera at the right target, so nothing else is owed.
		/// </summary>
		public static int Disarm()
		{
			int n = 0;
			foreach (Taken t in taken.Values)
			{
				if (t.Camera != null)
				{
					t.Camera.enabled = true;
					n++;
				}
				if (t.Renderer != null)
				{
					t.Renderer.enabled = true;
				}
			}
			taken.Clear();
			return n;
		}

		public static void Reset()
		{
			blits = 0;
		}

		public static void LogSummary(string what)
		{
			PerfLog.Info("lerpblit: " + what + " -- " + taken.Count + " cameras retired, " + blits + " blits"
				+ (failed ? ", SELF-CHECK FAILED, lever was off" : ""));
		}
	}
}
