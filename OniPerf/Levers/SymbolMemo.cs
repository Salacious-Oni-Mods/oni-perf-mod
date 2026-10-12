using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace OniPerf
{
	/// <summary>
	/// A dupe's symbol overrides (clothes, hair, accessories) re-derived only when something they
	/// are derived from has changed; the face, which follows the anim frame, still every time.
	///
	/// **The mechanism, measured.** At speed 1 with every other lever on,
	/// `KAnimBatchManager.UpdateDirty` is 1.3 ms of an 11.1 ms frame, and 0.93 of that is
	/// `SymbolOverrideController.ApplyOverrides`: 10.7 calls a frame at 87 us each. Every dupe
	/// prefab sets `applySymbolOverridesEveryFrame` (BaseMinionConfig), so each time a dupe's anim
	/// frame changes and its batch writes it, `KBatchedAnimController.ApplySymbolOverrides` runs
	/// the whole derivation: sort, resolve each target symbol's winning entry through two pooled
	/// dictionaries, find each source build's atlas, and write every frame of every overridden
	/// symbol into the controller's `SymbolOverrideInfoGpuData` (57 `SetSymbolOverrides` calls a
	/// dupe). Then it calls `FaceGraph.ApplyShape`, 0.095 of the 0.93, which picks eyes and mouth
	/// from the current anim frame and the blink and speech monitors.
	///
	/// Everything before the face is a function of the override list (its `version` moves on every
	/// add and remove), the controller's batch and that batch's atlas list, the batch group, and the
	/// GPU data object it writes into (replaced when the batch group changes). The GPU data is a
	/// byte array nothing clears between calls, so when none of those moved, the derivation writes
	/// the bytes that are already there.
	///
	/// **The lever.** A prefix on `ApplyOverrides` compares those inputs with the ones seen at this
	/// controller's last full derivation; if all match it runs only the face and skips the rest.
	/// Any change runs the original in full. **Every speed**: it only fires on frames where a dupe's
	/// anim is written, which is a colony-running cost.
	///
	/// **Exactness is checked, not assumed** (the <see cref="Verify"/> mode, used in testing): on
	/// every call the lever would skip, it runs the skip path (face only), copies the GPU bytes, runs the original
	/// in full, and compares. A mismatch means the face and the derivation disagree about a symbol
	/// (a blink, say), and is counted with the number of differing bytes. A call whose face changes
	/// the override list itself (talking: `SpeechMonitor.DrawMouth` adds the mouth override on every
	/// call) has no fair reference, because the original would then derive from the new list where
	/// vanilla derives from the old one; those are counted apart. The skip path matches vanilla on
	/// them: the bytes it leaves are the last derivation of the unchanged old list plus this face,
	/// and the version the face moved makes the next call derive in full.
	///
	/// Switched by the `symbolMemo` key in config.json (on by default).
	/// </summary>
	internal static class SymbolMemo
	{
		public static bool Active, Verify;

		private sealed class Memo
		{
			public bool Valid;
			public int Version, AtlasCount;
			public KAnimBatch Batch;
			public HashedString Group;
			public object Gpu;
		}

		private static readonly ConditionalWeakTable<SymbolOverrideController, Memo> memos
			= new ConditionalWeakTable<SymbolOverrideController, Memo>();
		private static AccessTools.FieldRef<SymbolOverrideController, KBatchedAnimController> animRef;
		private static AccessTools.FieldRef<SymbolOverrideController, FaceGraph> faceRef;
		private static FieldInfo gpuConverterField;
		private static PropertyInfo gpuProp;
		private static long ran, skipped, verified, mismatched, mismatchedBytes, faceMutated;
		private static byte[] pending;
		private static bool installed;

		public static void Install(Harmony harmony)
		{
			if (installed)
			{
				return;
			}
			var target = AccessTools.DeclaredMethod(typeof(SymbolOverrideController), "ApplyOverrides");
			try
			{
				animRef = AccessTools.FieldRefAccess<SymbolOverrideController, KBatchedAnimController>("animController");
				faceRef = AccessTools.FieldRefAccess<SymbolOverrideController, FaceGraph>("faceGraph");
				gpuConverterField = AccessTools.Field(typeof(SymbolOverrideInfoGpuData), "symbolOverrideInfoConverter");
				gpuProp = AccessTools.Property(typeof(KAnimControllerBase), "symbolOverrideInfoGpuData");
			}
			catch (Exception e)
			{
				PerfLog.Warn("symbolmemo: fields not found (" + e.Message + "); off");
				return;
			}
			if (target == null || gpuConverterField == null || gpuProp == null)
			{
				PerfLog.Warn("symbolmemo: ApplyOverrides/symbolOverrideInfoConverter/symbolOverrideInfoGpuData not found; off");
				return;
			}
			try
			{
				harmony.Patch(target, prefix: new HarmonyMethod(typeof(SymbolMemo), nameof(Prefix)),
					postfix: new HarmonyMethod(typeof(SymbolMemo), nameof(Postfix)));
				installed = true;
			}
			catch (Exception e)
			{
				PerfLog.Warn("symbolmemo: could not patch: " + e.Message);
				return;
			}
			PerfLog.Info("symbolmemo: installed on SymbolOverrideController.ApplyOverrides");
		}

		private static bool Prefix(SymbolOverrideController __instance)
		{
			pending = null;
			KBatchedAnimController anim = animRef(__instance);
			if ((!Active && !Verify) || anim == null)
			{
				ran++;
				return true;
			}
			KAnimBatch batch = anim.GetBatch();
			object gpu = gpuProp.GetValue(anim, null);
			Memo memo = memos.GetOrCreateValue(__instance);
			bool same = memo.Valid && memo.Version == __instance.version && ReferenceEquals(memo.Batch, batch)
				&& batch != null && memo.AtlasCount == batch.atlases.Count && memo.Group == anim.batchGroupID
				&& ReferenceEquals(memo.Gpu, gpu);
			if (same)
			{
				FaceGraph face = faceRef(__instance);
				int before = __instance.version;
				if (face != null)
				{
					face.ApplyShape();
				}
				if (!Verify)
				{
					skipped++;
					return false;
				}
				if (__instance.version != before)
				{
					// The face changed the override list itself (SpeechMonitor.DrawMouth adds the
					// talking mouth on every call). Vanilla derives from the list as it was before
					// the face, but the original run next would derive from the list after it, so
					// this call has no fair reference; the next call re-derives in full either way.
					faceMutated++;
				}
				else
				{
					// Verify: keep what the skip path produced, then let the original run in full
					// and compare in the postfix.
					byte[] bytes = Bytes(gpu);
					pending = bytes != null ? (byte[])bytes.Clone() : null;
				}
			}
			memo.Valid = true;
			memo.Version = __instance.version;
			memo.Batch = batch;
			memo.AtlasCount = batch != null ? batch.atlases.Count : -1;
			memo.Group = anim.batchGroupID;
			memo.Gpu = gpu;
			ran++;
			return true;
		}

		private static void Postfix(SymbolOverrideController __instance)
		{
			if (pending == null)
			{
				return;
			}
			KBatchedAnimController anim = animRef(__instance);
			byte[] full = anim != null ? Bytes(gpuProp.GetValue(anim, null)) : null;
			verified++;
			if (full == null || full.Length != pending.Length)
			{
				mismatched++;
			}
			else
			{
				int diff = 0;
				for (int i = 0; i < full.Length; i++)
				{
					if (full[i] != pending[i])
					{
						diff++;
					}
				}
				if (diff > 0)
				{
					mismatched++;
					mismatchedBytes += diff;
				}
			}
			pending = null;
		}

		private static byte[] Bytes(object gpu)
		{
			if (gpu == null)
			{
				return null;
			}
			var converter = (SymbolOverrideInfoGpuData.SymbolOverrideInfoToByteConverter)gpuConverterField.GetValue(gpu);
			return converter.bytes;
		}

		public static void Reset()
		{
			ran = skipped = verified = mismatched = mismatchedBytes = faceMutated = 0;
		}

		public static void LogSummary(string what)
		{
			PerfLog.Info("symbolmemo: " + what + " -- " + ran + " ran, " + skipped + " skipped"
				+ (Verify ? "; verify: " + verified + " checked, " + mismatched + " mismatched (" + mismatchedBytes + " bytes), " + faceMutated + " not comparable (face changed the list)" : ""));
		}
	}
}
