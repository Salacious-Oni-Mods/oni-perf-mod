using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Unity.Collections;

namespace OniPerf
{
	/// <summary>
	/// A dupe's symbol-override block copied into its batch texture only where it changed.
	///
	/// **The mechanism, measured.** With the debugger off and SymbolMemo on,
	/// `KAnimBatch.WriteSymbolOverrideInfoTex` is 0.37 ms of a 6–7 ms speed-1 frame: 12.9 calls a
	/// frame at 29 us each. Each dupe owns a `SymbolOverrideInfoGpuData`, a byte block of 48 bytes
	/// for every symbol frame of its batch group's builds, a few hundred KB. The batch keeps, per
	/// slot, which block it last copied and at which `version`, and copies the whole block again
	/// whenever the version moved. The version moves on every `SetSymbolOverrideInfo`, and the face
	/// (`FaceGraph.ApplyShape`) sets the eye and mouth symbols on every anim write, so each write of a
	/// dupe re-copies the whole block to change a few 48-byte entries.
	///
	/// The block has exactly three writers, all in `SymbolOverrideInfoGpuData`: the constructor
	/// (the whole block) and the two `SetSymbolOverrideInfo` overloads (a range of symbol indices,
	/// and one index). So the bytes that differ from the version a slot holds are the union of the
	/// ranges those two set since that version.
	///
	/// **The lever.** Postfixes on the two setters grow a per-block dirty range of symbol indices;
	/// the range is anchored to the version at the block's last copy (`WriteToTexture` postfix).
	/// A prefix on `WriteSymbolOverrideInfoTex` copies only the range when the slot already holds
	/// this block (same object) at exactly the anchored version, and records the slot as Klei's
	/// would; any other case (a new block, a block that moved slot, a block without a record) runs
	/// Klei's full copy. The texture upload (`Apply`) still happens, because the method still
	/// reports a write. **Every speed.**
	///
	/// **Exactness is checked, not assumed** (the <see cref="Verify"/> mode, used in testing): after each partial
	/// copy, the whole slot region is compared against the block, and differing bytes are counted.
	///
	/// Switched by the `overrideRange` key in config.json (on by default).
	/// </summary>
	internal static class OverrideRange
	{
		public static bool Active, Verify;

		private const int EntryBytes = 12 * 4;

		private sealed class Track
		{
			public int BaseVersion = int.MinValue;
			public int Min = int.MaxValue, Max = -1;
		}

		private static readonly ConditionalWeakTable<SymbolOverrideInfoGpuData, Track> tracks
			= new ConditionalWeakTable<SymbolOverrideInfoGpuData, Track>();
		private static AccessTools.FieldRef<KAnimBatch, KAnimBatch.SymbolOverrideInfoSlot[]> slotsRef;
		private static AccessTools.FieldRef<SymbolOverrideInfoGpuData, SymbolOverrideInfoGpuData.SymbolOverrideInfoToByteConverter> converterRef;
		private static long full, partial, bytesCopied, bytesSaved, verified, mismatched, mismatchedBytes;
		private static bool installed;

		public static void Install(Harmony harmony)
		{
			if (installed)
			{
				return;
			}
			var write = AccessTools.DeclaredMethod(typeof(KAnimBatch), "WriteSymbolOverrideInfoTex");
			var toTex = AccessTools.DeclaredMethod(typeof(SymbolOverrideInfoGpuData), "WriteToTexture");
			var setRange = AccessTools.DeclaredMethod(typeof(SymbolOverrideInfoGpuData), "SetSymbolOverrideInfo",
				new[] { typeof(int), typeof(int), typeof(int), typeof(KBatchGroupData), typeof(int), typeof(int) });
			var setOne = AccessTools.DeclaredMethod(typeof(SymbolOverrideInfoGpuData), "SetSymbolOverrideInfo",
				new[] { typeof(int), typeof(KAnim.Build.SymbolFrameInstance).MakeByRefType() });
			try
			{
				slotsRef = AccessTools.FieldRefAccess<KAnimBatch, KAnimBatch.SymbolOverrideInfoSlot[]>("symbolOverrideInfoSlots");
				converterRef = AccessTools.FieldRefAccess<SymbolOverrideInfoGpuData, SymbolOverrideInfoGpuData.SymbolOverrideInfoToByteConverter>("symbolOverrideInfoConverter");
			}
			catch (Exception e)
			{
				PerfLog.Warn("overriderange: fields not found (" + e.Message + "); off");
				return;
			}
			if (write == null || toTex == null || setRange == null || setOne == null)
			{
				PerfLog.Warn("overriderange: WriteSymbolOverrideInfoTex/WriteToTexture/SetSymbolOverrideInfo not found; off");
				return;
			}
			try
			{
				harmony.Patch(setRange, postfix: new HarmonyMethod(typeof(OverrideRange), nameof(SetRangePostfix)));
				harmony.Patch(setOne, postfix: new HarmonyMethod(typeof(OverrideRange), nameof(SetOnePostfix)));
				harmony.Patch(toTex, postfix: new HarmonyMethod(typeof(OverrideRange), nameof(ToTexPostfix)));
				harmony.Patch(write, prefix: new HarmonyMethod(typeof(OverrideRange), nameof(WritePrefix)));
				installed = true;
			}
			catch (Exception e)
			{
				PerfLog.Warn("overriderange: could not patch: " + e.Message);
				return;
			}
			PerfLog.Info("overriderange: installed on KAnimBatch.WriteSymbolOverrideInfoTex, SymbolOverrideInfoGpuData setters and WriteToTexture");
		}

		private static void Grow(SymbolOverrideInfoGpuData gpu, int min, int maxExclusive)
		{
			Track t;
			if (!tracks.TryGetValue(gpu, out t))
			{
				// No copy recorded yet: the next write is a full one whatever the range says.
				return;
			}
			if (min < t.Min)
			{
				t.Min = min;
			}
			if (maxExclusive > t.Max)
			{
				t.Max = maxExclusive;
			}
		}

		private static void SetRangePostfix(SymbolOverrideInfoGpuData __instance, int symbol_start_idx, int symbol_num_frames)
		{
			Grow(__instance, symbol_start_idx, symbol_start_idx + Math.Max(0, symbol_num_frames));
		}

		private static void SetOnePostfix(SymbolOverrideInfoGpuData __instance, int symbol_idx)
		{
			Grow(__instance, symbol_idx, symbol_idx + 1);
		}

		// Klei's full copy: the slot now holds the whole block at this version.
		private static void ToTexPostfix(SymbolOverrideInfoGpuData __instance)
		{
			Track t = tracks.GetOrCreateValue(__instance);
			t.BaseVersion = __instance.version;
			t.Min = int.MaxValue;
			t.Max = -1;
			full++;
		}

		private static bool WritePrefix(KAnimBatch __instance, int index, KAnimConverter.IAnimConverter controller,
			NativeArray<byte> data, ref bool __result)
		{
			if (!Active)
			{
				return true;
			}
			SymbolOverrideInfoGpuData gpu = controller.symbolOverrideInfoGpuData;
			KAnimBatch.SymbolOverrideInfoSlot[] slots = slotsRef(__instance);
			if (gpu == null || slots == null || index < 0 || index >= slots.Length)
			{
				return true;
			}
			KAnimBatch.SymbolOverrideInfoSlot slot = slots[index];
			if (!ReferenceEquals(slot.symbolOverrideInfo, gpu) || slot.dataVersion == gpu.version)
			{
				// Another block in this slot (full copy), or nothing changed (no copy): Klei's path.
				return true;
			}
			Track t;
			if (!tracks.TryGetValue(gpu, out t) || t.BaseVersion != slot.dataVersion)
			{
				return true;
			}
			byte[] bytes = converterRef(gpu).bytes;
			int start = index * bytes.Length;
			if (t.Max > t.Min)
			{
				int from = t.Min * EntryBytes;
				int len = Math.Min(bytes.Length, t.Max * EntryBytes) - from;
				if (len > 0)
				{
					NativeArray<byte>.Copy(bytes, from, data, start + from, len);
					bytesCopied += len;
					bytesSaved += bytes.Length - len;
				}
			}
			else
			{
				bytesSaved += bytes.Length;
			}
			slot.dataVersion = gpu.version;
			slots[index] = slot;
			t.BaseVersion = gpu.version;
			t.Min = int.MaxValue;
			t.Max = -1;
			partial++;
			if (Verify)
			{
				int diff = 0;
				for (int i = 0; i < bytes.Length; i++)
				{
					if (data[start + i] != bytes[i])
					{
						diff++;
					}
				}
				verified++;
				if (diff > 0)
				{
					mismatched++;
					mismatchedBytes += diff;
				}
			}
			__result = true;
			return false;
		}

		public static void Reset()
		{
			full = partial = bytesCopied = bytesSaved = verified = mismatched = mismatchedBytes = 0;
		}

		public static void LogSummary(string what)
		{
			PerfLog.Info("overriderange: " + what + " -- " + full + " full copies, " + partial + " partial; "
				+ (bytesCopied >> 10) + " KB copied, " + (bytesSaved >> 10) + " KB not copied"
				+ (Verify ? "; verify: " + verified + " checked, " + mismatched + " mismatched (" + mismatchedBytes + " bytes)" : ""));
		}
	}
}
