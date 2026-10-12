using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace OniPerf
{
	/// <summary>
	/// The camera rectangle the save restores, read out of the save's own bytes before the object
	/// loop builds anything, plus the one distance measure everything in the deferral is expressed in:
	/// how many screens a position is from the edge of that rectangle.
	///
	/// Why it is parsed here rather than taken from Klei. CameraSaveData.Load runs in
	/// SaveLoader.Load AFTER the object loop -- too late for a loader that wants to decide what
	/// to build. Its bytes are saveFileRoot.streamed["Camera"], which the prelude has already
	/// deserialized, and Klei builds a fresh FastReader over that array on every call, so reading
	/// it early is idempotent and costs 40 bytes: position, localScale, rotation, orthographic
	/// size. Parsing it ourselves rather than calling CameraSaveData.Load means no Klei static is
	/// set early and nothing downstream can see that we looked.
	///
	/// Two callers, one parse: LoadCensus attributes load time by this distance, DeferredSpawn
	/// decides by it. Arm is idempotent within a load so the log line appears once.
	/// </summary>
	internal static class LoadRect
	{
		private static float cx, cy, halfW, halfH;
		private static bool parsed;
		private static RectInt activeWorld;
		private static bool haveWorld;

		/// <summary>What Screens returns for a position on another world than the camera's: no pan
		/// reaches it, only a world switch. Finite so census rings and casts stay defined.</summary>
		public const float OffWorld = 1000f;

		/// <summary>True once a usable rectangle has been read. Anything missing or unreadable
		/// leaves it false rather than guessing a rect, and every caller is expected to switch
		/// itself off rather than fall back to a default view.</summary>
		public static bool Armed { get; private set; }

		public static float CentreX => cx;
		public static float CentreY => cy;
		public static float HalfWidth => halfW;
		public static float HalfHeight => halfH;

		/// <summary>Read the restored camera out of the save's streamed blobs. Klei's layout is
		/// CameraSaveData.Load's: position, localScale, rotation, orthographic size.</summary>
		public static void Arm(Dictionary<string, byte[]> streamed)
		{
			if (parsed)
			{
				return;
			}
			parsed = true;
			Armed = false;
			haveWorld = false;
			try
			{
				if (streamed == null || !streamed.TryGetValue("Camera", out byte[] blob) || blob == null || blob.Length < 40)
				{
					PerfLog.Warn("loadrect: the save has no readable Camera blob; anything that decides by distance from the camera is off");
					return;
				}
				var r = new FastReader(blob);
				Vector3 pos = r.ReadVector3();
				r.ReadVector3();
				r.ReadQuaternion();
				float ortho = r.ReadSingle();
				if (ortho <= 0f || float.IsNaN(ortho) || float.IsNaN(pos.x) || float.IsNaN(pos.y))
				{
					PerfLog.Warn(string.Format(CultureInfo.InvariantCulture,
						"loadrect: camera blob read as pos ({0:0.0}, {1:0.0}) ortho {2:0.00}; not usable", pos.x, pos.y, ortho));
					return;
				}
				float aspect = Screen.height > 0 ? (float)Screen.width / Screen.height : 16f / 9f;
				cx = pos.x;
				cy = pos.y;
				halfH = ortho;
				halfW = ortho * aspect;
				Armed = true;
				PerfLog.Info(string.Format(CultureInfo.InvariantCulture,
					"loadrect: camera restores to ({0:0.0}, {1:0.0}), orthographic half-height {2:0.0} cells, aspect {3:0.000} ({4}x{5}) -- a visible rect {6:0.0} x {7:0.0} cells, cells x {8:0.0}..{9:0.0} y {10:0.0}..{11:0.0}",
					cx, cy, halfH, aspect, Screen.width, Screen.height, halfW * 2f, halfH * 2f, cx - halfW, cx + halfW, cy - halfH, cy + halfH));
			}
			catch (Exception e)
			{
				PerfLog.Warn("loadrect: could not read the camera blob (" + e.Message + ")");
				Armed = false;
			}
		}

		/// <summary>
		/// The saved worlds' cell rects (WorldContainer worldOffset / worldSize, read by PreScan out
		/// of the object section). The one holding the camera centre is the world the camera
		/// restores to: Klei's camera shows only the active world (CameraController clamps to its
		/// bounds), so on a Spaced Out cluster every other asteroid and rocket interior is off
		/// screen however close its cells are. Without this a zoomed-out camera's rect (576 x 298
		/// cells on "Some light torture") reached across the whole 910 x 274 grid and nothing was
		/// deferred. A base-game save has one world over the whole grid, so nothing changes there.
		/// </summary>
		public static void SetWorlds(List<RectInt> worlds)
		{
			haveWorld = false;
			if (!Armed || worlds == null || worlds.Count == 0)
			{
				return;
			}
			int hits = 0;
			foreach (RectInt w in worlds)
			{
				if (cx >= w.xMin && cx < w.xMax && cy >= w.yMin && cy < w.yMax)
				{
					if (hits++ == 0)
					{
						activeWorld = w;
					}
				}
			}
			if (hits != 1)
			{
				PerfLog.Warn(string.Format(CultureInfo.InvariantCulture,
					"loadrect: the camera centre ({0:0.0}, {1:0.0}) is inside {2} of {3} saved worlds; distance stays camera-only", cx, cy, hits, worlds.Count));
				return;
			}
			haveWorld = true;
			PerfLog.Info(string.Format(CultureInfo.InvariantCulture,
				"loadrect: {0} saved world(s); the camera's is cells x {1}..{2} y {3}..{4}; positions on any other world count as off screen",
				worlds.Count, activeWorld.xMin, activeWorld.xMax, activeWorld.yMin, activeWorld.yMax));
		}

		/// <summary>Screens away from the edge of the visible rect: 0 inside it, 1 one screen out.
		/// Measured per axis and taken as the larger, so "one screen" means one pan in one
		/// direction. OffWorld for a position outside the camera's world (SetWorlds).</summary>
		public static float Screens(Vector3 p)
		{
			if (haveWorld && (p.x < activeWorld.xMin || p.x >= activeWorld.xMax || p.y < activeWorld.yMin || p.y >= activeWorld.yMax))
			{
				return OffWorld;
			}
			float dx = Mathf.Abs(p.x - cx) - halfW;
			float dy = Mathf.Abs(p.y - cy) - halfH;
			float sx = dx > 0f ? dx / (halfW * 2f) : 0f;
			float sy = dy > 0f ? dy / (halfH * 2f) : 0f;
			return Mathf.Max(sx, sy);
		}

		/// <summary>Reset for a second load in one process (the frontend can send us round again).</summary>
		public static void Reset()
		{
			parsed = false;
			Armed = false;
			haveWorld = false;
		}
	}
}
