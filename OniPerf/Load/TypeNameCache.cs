using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace OniPerf
{
	/// <summary>
	/// Caches Type.GetType(string) for the per-object load paths that call it: every state machine
	/// entry of every object (StateMachineSerializer, both entry formats) (SerializedList&lt;T&gt; also calls it,
	/// but it is an open generic Harmony cannot patch). Type.GetType parses the assembly-qualified name and searches assemblies on each call.
	///
	/// Resolution matches Klei's call site, which lives in Assembly-CSharp: Type.GetType searches the
	/// CALLING assembly and then mscorlib for a name with no assembly part, so such names are looked
	/// up in Assembly-CSharp first. Only hits are cached (a later-loaded assembly can still resolve a
	/// miss, as it would for Klei).
	///
	/// Switched by the `typeNameCache` key in config.json (on by default).
	/// </summary>
	internal static class TypeNameCache
	{
		private static readonly Dictionary<string, Type> cache = new Dictionary<string, Type>();
		private static readonly object gate = new object();
		private static readonly Assembly KleiAssembly = typeof(StateMachineSerializer).Assembly;
		private static readonly MethodInfo TypeGetType = AccessTools.Method(typeof(Type), nameof(Type.GetType), new[] { typeof(string) });
		public static long Hits, Misses;

		public static void Install(Harmony harmony)
		{
			int n = Patch(harmony);
			PerfLog.Info("typenamecache: installed on " + n + " state machine load method" + (n == 1 ? "" : "s"));
		}

		private static int Patch(Harmony harmony)
		{
			int patched = 0;
			var targets = new List<MethodBase>();
			foreach (Type t in new[] { typeof(StateMachineSerializer) })
			{
				foreach (Type nested in AllTypes(t))
				{
					foreach (MethodInfo m in nested.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
					{
						if (!m.IsAbstract && !m.ContainsGenericParameters && Calls(m))
						{
							targets.Add(m);
						}
					}
				}
			}
			foreach (MethodBase m in targets)
			{
				harmony.Patch(m, transpiler: new HarmonyMethod(typeof(TypeNameCache), nameof(Transpiler)));
				patched++;
			}
			return patched;
		}

		private static IEnumerable<Type> AllTypes(Type t)
		{
			yield return t;
			foreach (Type n in t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
			{
				foreach (Type x in AllTypes(n))
				{
					yield return x;
				}
			}
		}

		private static bool Calls(MethodInfo m)
		{
			try
			{
				foreach (var ins in PatchProcessor.GetOriginalInstructions(m))
				{
					if (ins.operand is MethodInfo called && called == TypeGetType)
					{
						return true;
					}
				}
			}
			catch (Exception)
			{
			}
			return false;
		}

		private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
		{
			MethodInfo get = AccessTools.Method(typeof(TypeNameCache), nameof(Get));
			foreach (CodeInstruction ins in instructions)
			{
				if (ins.operand is MethodInfo called && called == TypeGetType)
				{
					ins.operand = get;
				}
				yield return ins;
			}
		}

		public static Type Get(string name)
		{
			if (name == null)
			{
				return Type.GetType(name);
			}
			lock (gate)
			{
				if (cache.TryGetValue(name, out Type hit))
				{
					Hits++;
					return hit;
				}
			}
			Misses++;
			Type t = name.IndexOf(',') < 0 ? (KleiAssembly.GetType(name) ?? Type.GetType(name)) : Type.GetType(name);
			if (t != null)
			{
				lock (gate)
				{
					cache[name] = t;
				}
			}
			return t;
		}
	}
}
