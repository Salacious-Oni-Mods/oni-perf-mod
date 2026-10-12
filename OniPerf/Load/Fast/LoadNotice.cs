using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace OniPerf
{
	/// <summary>
	/// A notification in the top-left list while the defer-object drain builds the rest of the
	/// colony after the loading screen: "Loading colony: 62% (137,000 / 220,365)". Without it the
	/// only sign of the drain is counts like "Building broken" creeping up for a minute or two.
	///
	/// **How.** One vanilla Notification (Neutral, never expires, no sound) through
	/// NotificationManager. NotificationScreen keys its rows by `titleText`, which has a private
	/// setter, so the title stays fixed and the row's LocText is rewritten in place (at most four
	/// times a second); removal still finds the row by the unchanged title. The hover tooltip is
	/// computed live by the screen, so it carries the count and a time estimate too.
	/// </summary>
	internal static class LoadNotice
	{
		private const string Title = "Loading colony in background";
		private const double UpdateSec = 0.25;

		private static Notification note;
		private static int done, total, startDone;
		private static double began, lastUpdate = -1;
		private static string shown;
		private static readonly Stopwatch Clock = Stopwatch.StartNew();

		/// <summary>When the drain starts, with records already built and the total.</summary>
		public static void Show(int builtSoFar, int all)
		{
			Hide();
			if (NotificationManager.Instance == null)
			{
				return;
			}
			done = startDone = builtSoFar;
			total = all;
			began = Now();
			lastUpdate = -1;
			shown = null;
			note = new Notification(Title, NotificationType.Neutral, Tooltip, expires: false);
			note.playSound = false;
			NotificationManager.Instance.AddNotification(note);
		}

		/// <summary>Once a drain frame. Cheap when nothing is shown or the last write is recent.</summary>
		public static void Update(int builtSoFar)
		{
			if (note == null)
			{
				return;
			}
			done = builtSoFar;
			double now = Now();
			if (lastUpdate >= 0 && now - lastUpdate < UpdateSec)
			{
				return;
			}
			LocText text = Label();
			if (text == null)
			{
				// Not on screen yet: NotificationScreen adds a pending notification on its next Update.
				return;
			}
			lastUpdate = now;
			int pct = total > 0 ? (int)(100L * done / total) : 0;
			string line = string.Format(CultureInfo.InvariantCulture, "Loading colony: {0}% ({1:N0} / {2:N0}){3}", pct, done, total, Paused() ? ", paused" : "");
			if (line != shown)
			{
				if (shown == null)
				{
					PerfLog.Info("loadnotice: on screen, \"" + line + "\"");
				}
				text.text = line;
				shown = line;
			}
		}

		public static void Hide()
		{
			if (note == null)
			{
				return;
			}
			try
			{
				if (NotificationManager.Instance != null)
				{
					NotificationManager.Instance.RemoveNotification(note);
				}
			}
			catch (Exception e)
			{
				PerfLog.Warn("loadnotice: could not remove the notification: " + e.Message);
			}
			PerfLog.Info(string.Format(CultureInfo.InvariantCulture, "loadnotice: removed at {0:N0} / {1:N0}{2}", done, total, shown == null ? " (never reached the screen)" : ""));
			note = null;
		}

		private static string Tooltip(List<Notification> notes, object data)
		{
			string s = string.Format(CultureInfo.InvariantCulture,
				"The rest of the colony is still being built a few objects each frame.\nCounts such as \"Building broken\" keep growing until this finishes.{2}\n\n{0:N0} of {1:N0} built",
				done, total, Paused() ? "\nThe game stays paused until loading finishes." : "");
			double el = Now() - began;
			int made = done - startDone;
			if (made > 0 && el > 1.0)
			{
				double left = (total - done) * el / made;
				s += string.Format(CultureInfo.InvariantCulture, ", about {0:0} s left", Math.Max(0.0, left));
			}
			return s;
		}

		private static LocText Label()
		{
			NotificationScreen screen = NotificationScreen.Instance;
			if (screen == null)
			{
				return null;
			}
			if (!(Traverse.Create(screen).Field("entriesByMessage").GetValue() is IDictionary byMessage) || !byMessage.Contains(Title))
			{
				return null;
			}
			object entry = byMessage[Title];
			GameObject label = entry != null ? Traverse.Create(entry).Field("label").GetValue<GameObject>() : null;
			if (label == null)
			{
				return null;
			}
			HierarchyReferences refs = label.GetComponent<HierarchyReferences>();
			return refs != null && refs.HasReference("Text") ? refs.GetReference<LocText>("Text") : null;
		}

		private static bool Paused() => SpeedControlScreen.Instance != null && SpeedControlScreen.Instance.IsPaused;

		private static double Now() => Clock.ElapsedTicks / (double)Stopwatch.Frequency;
	}
}
