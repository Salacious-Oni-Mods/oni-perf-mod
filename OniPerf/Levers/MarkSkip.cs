using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace OniPerf
{
	/// <summary>
	/// `CellChangeMonitor.MarkDirty` stops descending into inactive children.
	///
	/// **The mechanism.** `SetPosition` calls `MarkDirty`, which puts the
	/// transform and every child under it, recursively, into the pending dirty set, active or not.
	/// Every dupe carries a `LaserEffect` object with 14 effect children (`BaseMinionConfig.
	/// SetupLaserEffects`), almost always all switched off. So each time a walking dupe moves, its
	/// switched-off effects are marked dirty, re-checked in `RenderEveryTick`, reported as moving,
	/// and their anim controllers' movement handlers put them on the anim updater's list.
	///
	/// **The lever.** A prefix that does the same walk but skips a child that is inactive
	/// (`activeSelf` false, so its whole subtree is inactive) and has no cell-changed handler. A
	/// child with a cell-changed handler is still marked, so no cell event is lost. What changes
	/// for a skipped object is only its movement state while it is switched off: it reads "not
	/// moving". When it is switched back on and moved, it is marked as usual. A transform passed to
	/// `MarkDirty` directly is always marked, active or not. **Every speed.**
	///
	/// Switched by the `markSkip` key in config.json (on by default).
	/// </summary>
	internal static class MarkSkip
	{
		public static bool Active;

		private static AccessTools.FieldRef<CellChangeMonitor, HybridListHashSet<int>> pendingRef;
		private static AccessTools.FieldRef<CellChangeMonitor, int> gridWidthRef;
		private static FieldInfo cellHandlersField;
		private static CellChangeMonitor boundTo;
		private static Func<int, bool> hasCellHandler;
		private static long marked, skipped;
		private static bool installed;

		public static void Install(Harmony harmony)
		{
			if (installed)
			{
				return;
			}
			var mark = AccessTools.DeclaredMethod(typeof(CellChangeMonitor), "MarkDirty");
			try
			{
				pendingRef = AccessTools.FieldRefAccess<CellChangeMonitor, HybridListHashSet<int>>("pendingDirtyTransforms");
				gridWidthRef = AccessTools.FieldRefAccess<CellChangeMonitor, int>("gridWidth");
				cellHandlersField = AccessTools.Field(typeof(CellChangeMonitor), "cellChangedHandlers");
			}
			catch (Exception e)
			{
				PerfLog.Warn("markskip: fields not found (" + e.Message + "); off");
				return;
			}
			if (mark == null || cellHandlersField == null)
			{
				PerfLog.Warn("markskip: CellChangeMonitor.MarkDirty or cellChangedHandlers not found; off");
				return;
			}
			try
			{
				harmony.Patch(mark, prefix: new HarmonyMethod(typeof(MarkSkip), nameof(Prefix)) { priority = Priority.First });
				installed = true;
			}
			catch (Exception e)
			{
				PerfLog.Warn("markskip: could not patch: " + e.Message);
				return;
			}
			PerfLog.Info("markskip: installed on CellChangeMonitor.MarkDirty");
		}

		// The handler dictionary's value type is private, so its ContainsKey is bound as a delegate
		// once per monitor instance rather than called through the boxing IDictionary interface.
		private static bool Bind(CellChangeMonitor monitor)
		{
			if (boundTo == monitor && hasCellHandler != null)
			{
				return true;
			}
			object dict = cellHandlersField.GetValue(monitor);
			MethodInfo containsKey = dict?.GetType().GetMethod("ContainsKey", new[] { typeof(int) });
			if (containsKey == null)
			{
				return false;
			}
			hasCellHandler = (Func<int, bool>)Delegate.CreateDelegate(typeof(Func<int, bool>), dict, containsKey);
			boundTo = monitor;
			return true;
		}

		private static bool Prefix(CellChangeMonitor __instance, Transform transform)
		{
			if (!Active || !Bind(__instance))
			{
				return true;
			}
			if (gridWidthRef(__instance) != 0)
			{
				Mark(pendingRef(__instance), transform);
			}
			return false;
		}

		private static void Mark(HybridListHashSet<int> pending, Transform transform)
		{
			pending.Add(transform.GetInstanceID());
			marked++;
			int childCount = transform.childCount;
			for (int i = 0; i < childCount; i++)
			{
				Transform child = transform.GetChild(i);
				if (!child.gameObject.activeSelf && !hasCellHandler(child.GetInstanceID()))
				{
					skipped++;
					continue;
				}
				Mark(pending, child);
			}
		}

		public static void Reset()
		{
			marked = skipped = 0;
		}

		public static void LogSummary(string what)
		{
			PerfLog.Info("markskip: " + what + " -- " + marked + " transforms marked, " + skipped + " inactive subtrees skipped");
		}
	}
}
