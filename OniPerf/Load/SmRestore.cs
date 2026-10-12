using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace OniPerf
{
	/// <summary>
	/// Cheaper strings for the game's state-machine restore, which runs once per saved state
	/// machine of every object at load.
	///
	/// Per saved parameter the game's restore reads two strings from the save, the context's type
	/// name and the parameter's name, rewrites an old version tag in the type name, and compares the
	/// type name with <c>GetType().FullName</c> of each candidate context. The type names are
	/// assembly-qualified generic names of about 400 characters, and a large colony saves over a
	/// million of them, so a load spends its time decoding, copying and comparing the same few
	/// hundred long strings again and again.
	///
	/// The restore itself is left as the game's own method. A transpiler swaps three calls inside
	/// it for cached ones that return strings with the same contents:
	///
	///   ReadKleiString      a string whose saved bytes were seen before is returned as the same
	///                       shared instance and the reader advanced past it, instead of decoded
	///                       anew; a new one is read by the game's own method, then shared;
	///   string.Replace      done once per shared type name, the result shared too;
	///   Type.FullName       done once per type, the result shared.
	///
	/// Equal contents are then also the same instance, so the comparison ends at its first,
	/// reference, check. Every value the restore sees has the contents it had before, so the
	/// parameter it picks is the one it picked before.
	///
	/// Switched by the `smRestore` key in config.json (on by default).
	/// </summary>
	internal static class SmRestore
	{
		public static long Hits, Misses;

		// Longest string kept: the type names are ~400 bytes; anything far longer is not one.
		private const int MaxCachedBytes = 4096;

		private sealed class Seen
		{
			public byte[] Bytes;
			public string Text;
		}

		private static readonly Dictionary<ulong, Seen> seen = new Dictionary<ulong, Seen>();
		private static readonly Dictionary<string, string> pool = new Dictionary<string, string>(StringComparer.Ordinal);
		private static readonly Dictionary<Type, string> fullNames = new Dictionary<Type, string>();
		private static readonly Dictionary<string, string> replaced = new Dictionary<string, string>(ByReference.Instance);
		private static string replacedFrom, replacedTo;

		private sealed class ByReference : IEqualityComparer<string>
		{
			public static readonly ByReference Instance = new ByReference();
			public bool Equals(string a, string b) => ReferenceEquals(a, b);
			public int GetHashCode(string s) => RuntimeHelpers.GetHashCode(s);
		}

		public static void Install(Harmony harmony)
		{
			Type entry = AccessTools.Inner(typeof(StateMachineSerializer), "Entry");
			MethodInfo restore = entry == null ? null : AccessTools.Method(entry, "Restore", new[] { typeof(StateMachine.Instance) });
			if (restore == null)
			{
				PerfLog.Warn("smrestore: no StateMachineSerializer.Entry.Restore; off");
				return;
			}
			int swapped = 0;
			foreach (CodeInstruction ins in PatchProcessor.GetOriginalInstructions(restore))
			{
				if (ins.operand is MethodInfo m && Swap(m) != null)
				{
					swapped++;
				}
			}
			// Two string reads, one Replace and one FullName in the build this was written for.
			if (swapped < 4)
			{
				PerfLog.Warn("smrestore: Entry.Restore has " + swapped + " of the 4 expected calls (game changed?); off");
				return;
			}
			harmony.Patch(restore, transpiler: new HarmonyMethod(typeof(SmRestore), nameof(Transpiler)));
			PerfLog.Info("smrestore: installed on StateMachineSerializer.Entry.Restore (" + swapped + " calls swapped)");
		}

		private static MethodInfo Swap(MethodInfo called)
		{
			if (called.Name == "ReadKleiString" && called.GetParameters().Length == 0
				&& (called.DeclaringType == typeof(FastReader) || called.DeclaringType == typeof(IReader)))
			{
				return AccessTools.Method(typeof(SmRestore), called.DeclaringType == typeof(FastReader) ? nameof(ReadFast) : nameof(ReadAny));
			}
			if (called.DeclaringType == typeof(string) && called.Name == nameof(string.Replace))
			{
				ParameterInfo[] p = called.GetParameters();
				if (p.Length == 2 && p[0].ParameterType == typeof(string) && p[1].ParameterType == typeof(string))
				{
					return AccessTools.Method(typeof(SmRestore), nameof(Replace));
				}
			}
			if (called.DeclaringType == typeof(Type) && called.Name == "get_FullName")
			{
				return AccessTools.Method(typeof(SmRestore), nameof(FullName));
			}
			return null;
		}

		private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
		{
			foreach (CodeInstruction ins in instructions)
			{
				if (ins.operand is MethodInfo m)
				{
					MethodInfo to = Swap(m);
					if (to != null)
					{
						// The instance on the stack becomes the static method's first argument.
						ins.opcode = OpCodes.Call;
						ins.operand = to;
					}
				}
				yield return ins;
			}
		}

		public static string ReadFast(FastReader reader) => Read(reader);

		public static string ReadAny(IReader reader) => Read(reader);

		private static string Read(IReader reader)
		{
			byte[] raw = reader.RawBytes();
			int pos = reader.Position;
			if (raw == null || pos < 0 || pos > raw.Length - 4)
			{
				return reader.ReadKleiString();
			}
			int len = BitConverter.ToInt32(raw, pos);
			int start = pos + 4;
			if (len < 0 || len > MaxCachedBytes || len > raw.Length - start)
			{
				return reader.ReadKleiString();
			}
			ulong h = Hash(raw, start, len);
			if (seen.TryGetValue(h, out Seen s) && Same(raw, start, len, s.Bytes))
			{
				Hits++;
				reader.SkipBytes(4 + len);
				return s.Text;
			}
			Misses++;
			string text = Share(reader.ReadKleiString());
			if (text != null)
			{
				var bytes = new byte[len];
				Buffer.BlockCopy(raw, start, bytes, 0, len);
				seen[h] = new Seen { Bytes = bytes, Text = text };
			}
			return text;
		}

		public static string Replace(string self, string from, string to)
		{
			if (from == null || to == null || !IsShared(self))
			{
				return self.Replace(from, to);
			}
			if (replacedFrom == null)
			{
				replacedFrom = from;
				replacedTo = to;
			}
			if (!string.Equals(from, replacedFrom, StringComparison.Ordinal) || !string.Equals(to, replacedTo, StringComparison.Ordinal))
			{
				return self.Replace(from, to);
			}
			if (!replaced.TryGetValue(self, out string result))
			{
				result = Share(self.Replace(from, to));
				replaced[self] = result;
			}
			return result;
		}

		public static string FullName(Type type)
		{
			if (type == null)
			{
				return null;
			}
			if (!fullNames.TryGetValue(type, out string name))
			{
				name = Share(type.FullName);
				fullNames[type] = name;
			}
			return name;
		}

		private static string Share(string s)
		{
			if (s == null)
			{
				return null;
			}
			if (pool.TryGetValue(s, out string shared))
			{
				return shared;
			}
			pool[s] = s;
			return s;
		}

		private static bool IsShared(string s)
		{
			return s != null && pool.TryGetValue(s, out string shared) && ReferenceEquals(shared, s);
		}

		private static ulong Hash(byte[] b, int start, int len)
		{
			ulong h = 14695981039346656037UL;
			for (int i = start, end = start + len; i < end; i++)
			{
				h = (h ^ b[i]) * 1099511628211UL;
			}
			return h ^ (ulong)len;
		}

		private static bool Same(byte[] raw, int pos, int len, byte[] want)
		{
			if (want.Length != len)
			{
				return false;
			}
			for (int i = 0; i < len; i++)
			{
				if (raw[pos + i] != want[i])
				{
					return false;
				}
			}
			return true;
		}

		public static string Report()
		{
			return Hits + Misses == 0 ? null
				: "smrestore: " + Hits.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " saved strings reused, "
					+ Misses.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " read";
		}

		public static void ResetCounters()
		{
			Hits = Misses = 0;
		}
	}
}
