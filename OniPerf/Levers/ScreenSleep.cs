using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace OniPerf
{
	/// <summary>
	/// The first measured lever: a screen that is closed should not cost anything per frame
	///.
	///
	/// A paused frame on the bench colony spends about 1.0 ms of 10.5 on UI the player is not looking at.
	/// Nothing dirties it -- `CanvasDirt.cs` measured zero graphic and zero layout rebuilds a
	/// frame -- so the cost is Unity's own per-frame walk of every active `Canvas` and `Graphic`
	/// that exists. **5,481 of the 5,763 active graphics belong to four closed screens:**
	/// `ResearchScreen` (4,505 in its subtree, of which 1,648 are the nested
	/// `ResearchScreenSideBar`), `AllResourcesScreen` 652 and `AllDiagnosticsScreen` 324.
	///
	/// They are closed and still active because of how Klei hides them. `KScreen.Show(false)`
	/// deactivates the GameObject, but two paths deliberately do not:
	///
	///   - `ShowOptimizedKScreen.Show` (AllResourcesScreen, AllDiagnosticsScreen, TableScreen)
	///     sets `canvasGroup.alpha = 0`, clears `blocksRaycasts` and disables the raycaster.
	///   - `ResearchScreen.Show` does the same by hand for the research tree.
	///
	/// The name says what it buys: hiding by alpha means reopening costs no rebuild. What it
	/// costs is that the subtree stays in Unity's canvas walk for the rest of the session.
	///
	/// **What this module does instead: disable the `Canvas` components in the subtree.** That is
	/// the middle option between Klei's (keep everything live) and deactivating the GameObject
	/// (cheapest per frame, and the reopen spike Klei was avoiding):
	///
	///   - A disabled `Canvas` leaves the canvas out of Unity's per-frame canvas work entirely.
	///   - Children stay active, so every `Awake`, `OnEnable`, `ISim1000ms` and refresh Klei
	///     wrote still runs, and a screen does not go stale while it sleeps.
	///   - Re-enabling raises `OnCanvasHierarchyChanged` on the subtree, which is Unity's own
	///     path for this, so the graphics re-register and redraw without a layout rebuild.
	///
	/// It does not catch everything the `nogo:` probe caught. Measured: this takes 0.69 ms of
	/// the 1.01 that deactivating the subtrees takes, and the difference is not canvas work at all
	/// -- it is 0.29 ms of `Update` on those screens' own components, which this module deliberately
	/// leaves running so nothing goes stale. That half is a separate lever with a separate risk
	/// (stale data on reopen) and gets measured on its own.
	///
	/// Switched by the `screenSleep` key in config.json (on by default).
	/// Two ways in: the `Show` hooks, which are the mechanism; and `SleepAll` / `WakeAll`, which
	/// put every closed screen to sleep or wake it at once, so one session can compare the two.
	///
	/// Waking is the part that has to be right, because a screen that sleeps and never wakes is
	/// an invisible screen. Three paths wake one: `Show(true)`, `Activate()`, and a watchdog on
	/// `KScreenManager.Update` that wakes any sleeping screen whose own `IsScreenActive()` says
	/// it is visible. The watchdog is what covers the paths we have not read.
	/// </summary>
	internal static class ScreenSleep
	{
		public static bool Enabled;

		/// <summary>
		/// False in probe mode: the hooks still wake a screen, but nothing falls asleep on its own,
		/// so a control window measures the frame Klei's code produces and the probe window
		/// measures the same colony with the screens asleep.
		/// </summary>
		public static bool AutoSleep = true;

		private sealed class Sleeper
		{
			public KScreen Screen;
			public List<Canvas> Canvases = new List<Canvas>();
			public List<MultiToggle> Toggles = new List<MultiToggle>();
			public List<Graphic> Fallen = new List<Graphic>();
			public List<Behaviour> Hover = new List<Behaviour>();
			public List<Behaviour> Scroll = new List<Behaviour>();
			public int Graphics;
		}

		/// <summary>
		/// Toggle half (<see cref="SleepToggles"/>): a sleeping screen's `MultiToggle`s are disabled too, and
		/// exactly those are re-enabled on wake. The Canvas half leaves every
		/// child component running so nothing goes stale; `MultiToggle` is the exception worth
		/// taking because its `Update` does nothing but count how long a click is held, and a
		/// disabled canvas receives no clicks. 639 of them tick in the bench colony. Disabling one with its
		/// GameObject still active runs no code of Klei's: its `OnDisable` acts only when the
		/// GameObject is inactive, and it has no enable hook.
		/// </summary>
		public static bool SleepToggles;
		private static int togglesAsleep;

		private static void SleepTogglesOf(Sleeper s)
		{
			if (s.Toggles.Count > 0 || s.Screen == null)
			{
				return;
			}
			foreach (MultiToggle t in s.Screen.GetComponentsInChildren<MultiToggle>(includeInactive: false))
			{
				if (t != null && t.enabled)
				{
					t.enabled = false;
					s.Toggles.Add(t);
				}
			}
			togglesAsleep += s.Toggles.Count;
		}

		private static void WakeTogglesOf(Sleeper s)
		{
			foreach (MultiToggle t in s.Toggles)
			{
				if (t != null)
				{
					t.enabled = true;
				}
			}
			togglesAsleep -= s.Toggles.Count;
			s.Toggles.Clear();
		}

		/// <summary>
		/// Graphics half (<see cref="SleepGraphics"/>): a sleeping screen's graphics that the Canvas half cannot
		/// take out are disabled, and exactly those are re-enabled on wake.
		///
		/// A graphic belongs to the nearest *enabled* Canvas above it. Disabling a nested canvas
		/// therefore does not take its graphics out of anything: they fall through to the next
		/// enabled canvas up. `AllResourcesScreen` and `AllDiagnosticsScreen` sit under the HUD root
		/// (`ssOverlayCanvas`) with no root canvas of their own, so the Canvas half moved their 974
		/// graphics onto the HUD root instead of removing them. There they stay in the root canvas
		/// walk and in the EventSystem's per-frame raycast, which tests every raycast target on the
		/// canvas: 0.35 ms a paused frame. Only `ResearchScreen`, whose top canvas is a root canvas,
		/// was really asleep.
		///
		/// Disabling a `Graphic` unregisters it from its canvas and from the raycast registry. Klei's
		/// code in these screens shows and hides rows with `SetActive`, never `Graphic.enabled`
		/// so re-enabling exactly the recorded set restores what Klei left. Only
		/// graphics whose nearest enabled canvas is outside the screen are touched; a graphic that
		/// becomes active while the screen sleeps (a row added for a new resource) is not, and costs
		/// what it costs today. Waking re-dirties each graphic once, the rebuild Klei's alpha hiding
		/// avoided, paid when the screen opens.
		///
		/// **This half defaults on** (the other three default off; this mod switches all four on),
		/// because a measurement priced it: one visit to vanilla Consumables leaves +17.95 ms on every later frame with
		/// the Canvas half alone, and +4.61 ms with this half as well -- 13.34 ms, 74% of the cost,
		/// from this flag and nothing else (two runs that differ only here). The Canvas half
		/// cannot do it alone for the reason above: the screen has one canvas of its own, so
		/// disabling it hands ~9,700 graphics up to the HUD root rather than removing them.
		///
		/// What it does not buy: the frame while the screen is OPEN (+20.14 ms, and an open screen
		/// is awake by definition) or the 5.40 s build on first open. Both need more work.
		/// </summary>
		public static bool SleepGraphics = true;
		private static int graphicsAsleep;

		private static void SleepGraphicsOf(Sleeper s)
		{
			if (s.Fallen.Count > 0 || s.Screen == null)
			{
				return;
			}
			Transform screen = s.Screen.transform;
			foreach (Graphic g in s.Screen.GetComponentsInChildren<Graphic>(includeInactive: false))
			{
				if (g == null || !g.enabled)
				{
					continue;
				}
				Transform owner = null;
				for (Transform t = g.transform; t != null; t = t.parent)
				{
					Canvas c = t.GetComponent<Canvas>();
					if (c != null && c.enabled)
					{
						owner = t;
						break;
					}
				}
				if (owner != null && owner != screen && !owner.IsChildOf(screen))
				{
					g.enabled = false;
					s.Fallen.Add(g);
				}
			}
			graphicsAsleep += s.Fallen.Count;
		}

		private static void WakeGraphicsOf(Sleeper s)
		{
			foreach (Graphic g in s.Fallen)
			{
				if (g != null)
				{
					g.enabled = true;
				}
			}
			graphicsAsleep -= s.Fallen.Count;
			s.Fallen.Clear();
		}

		/// <summary>
		/// Hover half (<see cref="SleepHover"/>): a sleeping screen's `LineLayer`s (graph lines, `SparkLayer`
		/// included) and `AlternateSiblingColor`s are disabled, and exactly those re-enabled on wake.
		/// Their `Update`s do nothing a closed screen needs: `LineLayer.Update`
		/// only moves the point highlight under the mouse, and `AlternateSiblingColor.Update` only
		/// recolours a row whose sibling index changed, which its first `Update` after wake catches
		/// up on. Neither type, nor `GraphLayer`, has an enable or disable hook, and nothing in the
		/// game code reads their `enabled`. 68 and 15 of them tick behind the two sleeping list
		/// screens in the bench colony.
		/// </summary>
		public static bool SleepHover;
		private static int hoverAsleep;

		private static void SleepHoverOf(Sleeper s)
		{
			if (s.Hover.Count > 0 || s.Screen == null)
			{
				return;
			}
			foreach (Behaviour b in s.Screen.GetComponentsInChildren<LineLayer>(includeInactive: false))
			{
				if (b != null && b.enabled)
				{
					b.enabled = false;
					s.Hover.Add(b);
				}
			}
			foreach (Behaviour b in s.Screen.GetComponentsInChildren<AlternateSiblingColor>(includeInactive: false))
			{
				if (b != null && b.enabled)
				{
					b.enabled = false;
					s.Hover.Add(b);
				}
			}
			hoverAsleep += s.Hover.Count;
		}

		private static void WakeHoverOf(Sleeper s)
		{
			foreach (Behaviour b in s.Hover)
			{
				if (b != null)
				{
					b.enabled = true;
				}
			}
			hoverAsleep -= s.Hover.Count;
			s.Hover.Clear();
		}

		/// <summary>The `sleephover` probe: the hover half on or off for every screen already asleep.</summary>
		public static int SetHoverAll(bool on)
		{
			SleepHover = on;
			foreach (Sleeper s in asleep)
			{
				if (on)
				{
					SleepHoverOf(s);
				}
				else
				{
					WakeHoverOf(s);
				}
			}
			return hoverAsleep;
		}

		/// <summary>
		/// Scroll half (<see cref="SleepScroll"/>): a sleeping screen's `ScrollRect`s (`KScrollRect`
		/// included) are disabled, and exactly those re-enabled on wake.
		/// `KScrollRect.LateUpdate` ends in `ScrollRect.LateUpdate`, which recomputes the content
		/// bounds, the scrollbars and the velocity every frame whether or not anything can be seen:
		/// 0.10 ms a paused frame for four instances in the bench colony, three of them behind the sleeping
		/// resource and diagnostics lists. Disabling a `ScrollRect` keeps the content's position;
		/// it drops the velocity and the scrollbar listeners, and `OnEnable` puts the listeners back
		/// and forces one layout pass on the first `LateUpdate`, which is a one-off on wake.
		/// </summary>
		public static bool SleepScroll;
		private static int scrollAsleep;

		private static void SleepScrollOf(Sleeper s)
		{
			if (s.Scroll.Count > 0 || s.Screen == null)
			{
				return;
			}
			foreach (Behaviour b in s.Screen.GetComponentsInChildren<ScrollRect>(includeInactive: false))
			{
				if (b != null && b.enabled)
				{
					b.enabled = false;
					s.Scroll.Add(b);
				}
			}
			scrollAsleep += s.Scroll.Count;
		}

		private static void WakeScrollOf(Sleeper s)
		{
			foreach (Behaviour b in s.Scroll)
			{
				if (b != null)
				{
					b.enabled = true;
				}
			}
			scrollAsleep -= s.Scroll.Count;
			s.Scroll.Clear();
		}

		/// <summary>The `sleepscroll` probe: the scroll half on or off for every screen already asleep.</summary>
		public static int SetScrollAll(bool on)
		{
			SleepScroll = on;
			foreach (Sleeper s in asleep)
			{
				if (on)
				{
					SleepScrollOf(s);
				}
				else
				{
					WakeScrollOf(s);
				}
			}
			return scrollAsleep;
		}

		/// <summary>The `sleepgraphics` probe: the graphics half on or off for every screen already asleep.</summary>
		public static int SetGraphicsAll(bool on)
		{
			SleepGraphics = on;
			foreach (Sleeper s in asleep)
			{
				if (on)
				{
					SleepGraphicsOf(s);
				}
				else
				{
					WakeGraphicsOf(s);
				}
			}
			return graphicsAsleep;
		}

		/// <summary>The `sleeptoggles` probe: the toggle half on or off for every screen already asleep.</summary>
		public static int SetTogglesAll(bool on)
		{
			SleepToggles = on;
			foreach (Sleeper s in asleep)
			{
				if (on)
				{
					SleepTogglesOf(s);
				}
				else
				{
					WakeTogglesOf(s);
				}
			}
			return togglesAsleep;
		}

		private static readonly List<Sleeper> asleep = new List<Sleeper>();

		/// <summary>
		/// Distinct graphics across all sleepers. A KScreen can nest inside another -- 
		/// `ResearchScreenSideBar` sits under `ResearchScreen` -- so per-screen subtree counts
		/// double-count the overlap, and summing them reported 7,129 where 5,481 graphics exist.
		/// Disabling is not affected, because a canvas already disabled is skipped, but the figure
		/// that gets written down has to be the real one.
		/// </summary>
		private static readonly HashSet<int> sleepingGraphics = new HashSet<int>();
		private static int woken, slept, wokenByWatchdog;
		private static bool hooksArmed;

		/// <summary>
		/// The watchdog runs on KScreenManager.Update, which is once a frame, so it must not walk
		/// the scene. It walks the sleeping list -- a handful of entries -- and only every 30th
		/// frame. Sleeping is rare and waking one frame late is invisible.
		/// </summary>
		private const int WatchdogEveryFrames = 30;
		private static int watchdogTick;

		public static void Install(Harmony harmony)
		{
			Type kScreen = AccessTools.TypeByName("KScreen");
			if (kScreen == null)
			{
				PerfLog.Warn("screensleep: no KScreen type; off");
				return;
			}

			// Every Show(bool) in the hierarchy, found rather than listed: Harmony patches the
			// exact method, so a subclass that overrides Show is not covered by a patch on the
			// base. There are eight overrides in 744825 and a list would go stale on the next game
			// update, or miss another mod's screen.
			int patched = 0, targets = 0;
			foreach (Type t in ScreenTypes(kScreen))
			{
				MethodInfo show = AccessTools.DeclaredMethod(t, "Show", new[] { typeof(bool) });
				if (show == null || show.IsAbstract)
				{
					continue;
				}
				targets++;
				try
				{
					harmony.Patch(show,
						prefix: new HarmonyMethod(typeof(ScreenSleep), nameof(ShowPrefix)),
						postfix: new HarmonyMethod(typeof(ScreenSleep), nameof(ShowPostfix)));
					patched++;
				}
				catch (Exception e)
				{
					PerfLog.Warn("screensleep: could not patch " + t.Name + ".Show: " + e.Message);
				}
			}

			MethodInfo activate = AccessTools.DeclaredMethod(kScreen, "Activate", Type.EmptyTypes);
			if (activate != null)
			{
				try
				{
					harmony.Patch(activate, prefix: new HarmonyMethod(typeof(ScreenSleep), nameof(ActivatePrefix)));
				}
				catch (Exception e)
				{
					PerfLog.Warn("screensleep: could not patch KScreen.Activate: " + e.Message);
				}
			}

			MethodInfo managerUpdate = AccessTools.DeclaredMethod(
				AccessTools.TypeByName("KScreenManager"), "Update", Type.EmptyTypes);
			if (managerUpdate != null)
			{
				try
				{
					harmony.Patch(managerUpdate, postfix: new HarmonyMethod(typeof(ScreenSleep), nameof(Watchdog)));
				}
				catch (Exception e)
				{
					PerfLog.Warn("screensleep: could not patch KScreenManager.Update: " + e.Message);
				}
			}
			else
			{
				PerfLog.Warn("screensleep: no KScreenManager.Update; the wake watchdog is off");
			}

			hooksArmed = patched > 0;
			PerfLog.Info("screensleep: hooked " + patched + " of " + targets + " Show(bool) methods");
		}

		private static IEnumerable<Type> ScreenTypes(Type kScreen)
		{
			// KScreen lives in Assembly-CSharp-firstpass and its subclasses mostly in
			// Assembly-CSharp, so the search is over loaded assemblies rather than one. A type
			// that fails to load is skipped: a broken third-party assembly must not take the mod
			// down with it.
			var found = new List<Type> { kScreen };
			foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
			{
				Type[] types;
				try
				{
					types = a.GetTypes();
				}
				catch (Exception)
				{
					continue;
				}
				foreach (Type t in types)
				{
					if (t != kScreen && kScreen.IsAssignableFrom(t))
					{
						found.Add(t);
					}
				}
			}
			return found;
		}

		// ---- the hooks -------------------------------------------------------------------

		private static void ShowPrefix(KScreen __instance, bool show)
		{
			if (Enabled && show)
			{
				// Before the body, because Show and OnShow read rects and set up layout, and they
				// should see the screen exactly as Klei's code left it.
				Wake(__instance);
			}
		}

		private static void ShowPostfix(KScreen __instance, bool show)
		{
			if (!Enabled || !AutoSleep || show || __instance == null)
			{
				return;
			}
			// After the body, and only if the screen is still active: a Show(false) that
			// deactivated the GameObject is already free, and there is nothing here to improve.
			if (__instance.gameObject != null && __instance.gameObject.activeInHierarchy)
			{
				Sleep(__instance);
			}
		}

		private static void ActivatePrefix(KScreen __instance)
		{
			if (Enabled)
			{
				Wake(__instance);
			}
		}

		/// <summary>
		/// The Show hooks only sleep a screen when it *closes*. A screen that starts closed and is
		/// never opened is never slept, and in the measured save that left two of four closed
		/// screens awake, worth ~0.5 ms of a 6.5 ms paused frame. So auto mode
		/// also sweeps once per loaded game, a few hundred frames after `Game.Instance` appears so
		/// the HUD's screens have finished their own startup Show calls.
		/// </summary>
		private const int LoadSweepAfterFrames = 300;
		private static int loadSweepGame, loadSweepFrames;

		private static void LoadSweep()
		{
			if (!AutoSleep || Game.Instance == null)
			{
				return;
			}
			int game = Game.Instance.GetInstanceID();
			if (game == loadSweepGame)
			{
				return;
			}
			if (++loadSweepFrames < LoadSweepAfterFrames)
			{
				return;
			}
			loadSweepGame = game;
			loadSweepFrames = 0;
			PerfLog.Info("screensleep: load sweep");
			SleepAll();
		}

		private static void Watchdog()
		{
			if (Enabled)
			{
				LoadSweep();
			}
			if (!Enabled || asleep.Count == 0 || ++watchdogTick < WatchdogEveryFrames)
			{
				return;
			}
			watchdogTick = 0;
			for (int i = asleep.Count - 1; i >= 0; i--)
			{
				Sleeper s = asleep[i];
				if (s.Screen == null)
				{
					asleep.RemoveAt(i);
					continue;
				}
				bool visible;
				try
				{
					visible = s.Screen.IsScreenActive();
				}
				catch (Exception)
				{
					// A screen that throws while being asked whether it is visible is a screen we
					// should not be holding asleep.
					visible = true;
				}
				if (visible)
				{
					wokenByWatchdog++;
					PerfLog.Info("screensleep: watchdog woke " + s.Screen.name
						+ " -- it became visible without Show(true) or Activate()");
					Wake(s.Screen);
				}
			}
		}

		// ---- sleep and wake --------------------------------------------------------------

		public static void Sleep(KScreen screen)
		{
			if (screen == null || Find(screen) != null)
			{
				return;
			}
			var s = new Sleeper { Screen = screen };
			Canvas[] canvases;
			Graphic[] graphics;
			try
			{
				canvases = screen.GetComponentsInChildren<Canvas>(includeInactive: false);
				graphics = screen.GetComponentsInChildren<Graphic>(includeInactive: false);
			}
			catch (Exception e)
			{
				PerfLog.Warn("screensleep: could not scan " + screen.name + ": " + e.Message);
				return;
			}
			s.Graphics = graphics.Length;
			foreach (Graphic g in graphics)
			{
				if (g != null)
				{
					sleepingGraphics.Add(g.GetInstanceID());
				}
			}
			foreach (Canvas c in canvases)
			{
				if (c != null && c.enabled)
				{
					c.enabled = false;
					s.Canvases.Add(c);
				}
			}
			if (s.Canvases.Count == 0)
			{
				// The screen's graphics hang off a canvas above it, which this module will not
				// touch: disabling a shared canvas would hide screens that are open. Recorded, not
				// silently skipped, because it is the reason a screen shows no win.
				return;
			}
			asleep.Add(s);
			slept++;
			if (SleepToggles)
			{
				SleepTogglesOf(s);
			}
			if (SleepGraphics)
			{
				SleepGraphicsOf(s);
			}
			if (SleepHover)
			{
				SleepHoverOf(s);
			}
			if (SleepScroll)
			{
				SleepScrollOf(s);
			}
		}

		public static void Wake(KScreen screen)
		{
			Sleeper s = Find(screen);
			if (s == null)
			{
				return;
			}
			foreach (Canvas c in s.Canvases)
			{
				if (c != null)
				{
					c.enabled = true;
				}
			}
			WakeGraphicsOf(s);
			WakeHoverOf(s);
			WakeScrollOf(s);
			WakeTogglesOf(s);
			asleep.Remove(s);
			woken++;
			if (asleep.Count == 0)
			{
				sleepingGraphics.Clear();
			}
		}

		private static Sleeper Find(KScreen screen)
		{
			for (int i = asleep.Count - 1; i >= 0; i--)
			{
				if (asleep[i].Screen == null)
				{
					asleep.RemoveAt(i);
				}
				else if (ReferenceEquals(asleep[i].Screen, screen))
				{
					return asleep[i];
				}
			}
			return null;
		}

		/// <summary>
		/// Put every closed screen to sleep now. This is what the `sleep` probe calls, so that one
		/// run measures the same colony both ways; the Show hooks reach the same state by the path
		/// that would ship. Returns the number of screens slept.
		/// </summary>
		public static int SleepAll()
		{
			int before = asleep.Count;
			KScreen[] screens = UnityEngine.Object.FindObjectsByType<KScreen>(FindObjectsSortMode.None);
			foreach (KScreen screen in screens)
			{
				bool visible;
				try
				{
					visible = screen.IsScreenActive();
				}
				catch (Exception)
				{
					visible = true;
				}
				if (!visible)
				{
					Sleep(screen);
				}
			}
			int n = asleep.Count - before;
			int subtreeSum = 0;
			foreach (Sleeper s in asleep)
			{
				subtreeSum += s.Graphics;
			}
			PerfLog.Info("screensleep: slept " + n + " closed screens (" + asleep.Count + " asleep, "
				+ sleepingGraphics.Count + " distinct graphics out of the canvas walk"
				+ (subtreeSum != sleepingGraphics.Count
					? "; subtrees sum to " + subtreeSum + ", so " + (subtreeSum - sleepingGraphics.Count)
						+ " are in a screen nested inside another"
					: "") + ")");
			foreach (Sleeper s in asleep)
			{
				if (s.Graphics >= 50)
				{
					PerfLog.Info(string.Format(CultureInfo.InvariantCulture,
						"screensleep:   {0,-40} {1,5} graphics in its subtree, {2} canvases disabled here",
						s.Screen != null ? s.Screen.name : "(destroyed)", s.Graphics, s.Canvases.Count));
				}
			}
			return n;
		}

		/// <summary>
		/// Wake everything, and say so per screen. The count is checked by the caller: a probe
		/// that left a canvas disabled would leave the rest of the run measuring a frame with
		/// missing UI, and that is exactly the kind of silent leak that poisons a table.
		/// </summary>
		public static int WakeAll()
		{
			int n = 0, stillDisabled = 0;
			var list = new List<Sleeper>(asleep);
			foreach (Sleeper s in list)
			{
				if (s.Screen == null)
				{
					asleep.Remove(s);
					continue;
				}
				Wake(s.Screen);
				n++;
				foreach (Canvas c in s.Canvases)
				{
					if (c != null && !c.enabled)
					{
						stillDisabled++;
					}
				}
			}
			if (stillDisabled > 0)
			{
				PerfLog.Warn("screensleep: " + stillDisabled + " canvases are still disabled after WakeAll");
			}
			PerfLog.Info("screensleep: woke " + n + " screens; " + asleep.Count + " still asleep");
			return n;
		}

		public static int AsleepCount => asleep.Count;

		public static void LogSummary()
		{
			if (!Enabled && !hooksArmed)
			{
				return;
			}
			// Which halves were on belongs in the summary: `Enabled` alone arms the Canvas
			// half, the four half switches are separate statics, and a run that meant to measure a half
			// but never switched it on would otherwise look, in the log, exactly like a run that did.
			PerfLog.Info("screensleep: " + slept + " sleeps, " + woken + " wakes ("
				+ wokenByWatchdog + " by the watchdog), " + asleep.Count + " asleep now; halves"
				+ " graphics=" + (SleepGraphics ? graphicsAsleep + " disabled" : "off")
				+ " toggles=" + (SleepToggles ? "on" : "off")
				+ " hover=" + (SleepHover ? "on" : "off")
				+ " scroll=" + (SleepScroll ? "on" : "off"));
		}
	}
}
