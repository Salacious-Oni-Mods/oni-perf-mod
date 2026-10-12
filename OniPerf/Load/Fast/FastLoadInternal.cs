// Adapted from the game's own code and NOT covered by the MIT licence: see NOTICE.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using KSerialization;
using UnityEngine;

namespace OniPerf
{
	/// <summary>
	/// SaveLoadRoot.LoadInternal (the game's SaveLoadRoot.cs 227-310, private static), same control flow,
	/// with the per-entry lookups moved into LoadLookup (names interned per load, component indices
	/// cached per component-type layout) and a timer on each part so the "deserialize" phase of
	/// AsyncLoad can be split:
	///   lookup     reading the component header + matching it to a component; includes
	///   components GetComponents + the layout lookup, once per object
	///   managers   the ISerializableComponentManager path
	///   mapping    Deserializer.DeserializeTypeless (FastDeser or Klei's, whichever is installed)
	///   details    ISaveLoadableDetails.Deserialize
	/// The same code serves nested calls (Storage's details load the stored items through
	/// SaveLoadRoot.Load) through a prefix, active only between Prepare and End; per-call state lives
	/// in one frame per nesting depth, and only the outermost call is timed.
	/// </summary>
	internal static class FastLoadInternal
	{
		private static readonly FieldInfo ManagersField = AccessTools.Field(typeof(SaveLoadRoot), "serializableComponentManagers");
		private static readonly FieldInfo TypeToStringField = AccessTools.Field(typeof(SaveLoadRoot), "sTypeToString");
		private static readonly FieldInfo OptionalField = AccessTools.Field(typeof(SaveLoadRoot), "m_optionalComponentTypeNames");

		private static Dictionary<string, ISerializableComponentManager> managers;
		private static Dictionary<Type, string> typeToString;
		private static bool active;
		private static int depth;
		private static readonly List<Frame> frames = new List<Frame>();
		private static readonly Func<string, object> ResolveManager = text =>
			managers != null && managers.TryGetValue(text, out ISerializableComponentManager m) ? m : null;

		public static long LookupTicks, ComponentsTicks, ManagerTicks, MappingTicks, DetailsTicks, NestedCalls;
		public static readonly Dictionary<string, long[]> ByComponent = new Dictionary<string, long[]>();

		public static bool Ready => ManagersField != null && TypeToStringField != null && OptionalField != null;

		private sealed class Frame
		{
			public readonly List<KMonoBehaviour> comps = new List<KMonoBehaviour>();
			public Type[] types = new Type[64];
			public readonly LoadLookup.Seen seen = new LoadLookup.Seen();
		}

		public static void Install(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(SaveLoadRoot), "LoadInternal"),
				prefix: new HarmonyMethod(typeof(FastLoadInternal), nameof(LoadInternalPrefix)));
		}

		/// <summary>Called once per load, before the object loop: SaveLoadRoot's statics are set up by then.</summary>
		public static void Prepare()
		{
			managers = (Dictionary<string, ISerializableComponentManager>)ManagersField.GetValue(null);
			typeToString = (Dictionary<Type, string>)TypeToStringField.GetValue(null);
			LoadLookup.Reset();
			LookupTicks = ComponentsTicks = ManagerTicks = MappingTicks = DetailsTicks = NestedCalls = 0;
			ByComponent.Clear();
			depth = 0;
			active = true;
		}

		/// <summary>On again for one deferred record (the whole-object deferral's drain) without a Reset: the names and
		/// layouts the loop interned stay valid for the same save. Returns whether it was already on,
		/// so a nested caller leaves it as it found it.</summary>
		public static bool Resume()
		{
			bool was = active;
			active = true;
			return was;
		}

		/// <summary>After the object loop: nested calls go back to Klei's method.</summary>
		public static void End()
		{
			active = false;
		}

		private static bool LoadInternalPrefix(GameObject gameObject, IReader reader)
		{
			if (!active)
			{
				return true;
			}
			NestedCalls++;
			return !TryLoad(gameObject, reader);
		}

		/// <summary>False (nothing read) when the reader has no backing array; the caller then runs Klei's method.</summary>
		public static bool TryLoad(GameObject gameObject, IReader reader)
		{
			byte[] raw = reader.RawBytes();
			if (raw == null || typeToString == null)
			{
				return false;
			}
			if (depth == frames.Count)
			{
				frames.Add(new Frame());
			}
			Frame frame = frames[depth];
			bool top = depth == 0;
			depth++;
			try
			{
				Load(gameObject, reader, raw, frame, top);
			}
			finally
			{
				depth--;
			}
			return true;
		}

		private static void Load(GameObject gameObject, IReader reader, byte[] raw, Frame frame, bool top)
		{
			long t = top ? Stopwatch.GetTimestamp() : 0;
			frame.seen.Clear();
			List<KMonoBehaviour> comps = frame.comps;
			LoadLookup.Layout layout = null;
			if (gameObject != null)
			{
				gameObject.GetComponents(comps);
				int count = comps.Count;
				if (frame.types.Length < count)
				{
					frame.types = new Type[count * 2];
				}
				for (int j = 0; j < count; j++)
				{
					frame.types[j] = comps[j].GetType();
				}
				layout = LoadLookup.GetLayout(frame.types, count, typeToString);
				Array.Clear(frame.types, 0, count);
				if (top)
				{
					ComponentsTicks += Stopwatch.GetTimestamp() - t;
				}
			}
			int num = reader.ReadInt32();
			for (int i = 0; i < num; i++)
			{
				LoadLookup.Name name = LoadLookup.ReadName(raw, reader.Position, out int consumed, ResolveManager);
				reader.SkipBytes(consumed);
				if (name == null)
				{
					// Klei: serializableComponentManagers.TryGetValue(null, ...).
					throw new ArgumentNullException("key");
				}
				string text = name.text;
				int num2 = reader.ReadInt32();
				int position = reader.Position;
				if (name.manager is ISerializableComponentManager manager)
				{
					if (top)
					{
						long m0 = Stopwatch.GetTimestamp(), mb0 = GC.GetTotalMemory(false);
						LookupTicks += m0 - t;
						manager.Deserialize(gameObject, reader);
						t = Stopwatch.GetTimestamp();
						ManagerTicks += t - m0;
						Charge(text, t - m0, GC.GetTotalMemory(false) - mb0);
					}
					else
					{
						manager.Deserialize(gameObject, reader);
					}
					continue;
				}
				int value2 = frame.seen.Get(name.id);
				KMonoBehaviour kMonoBehaviour = null;
				int num3 = 0;
				if (layout != null)
				{
					int j = LoadLookup.Pick(LoadLookup.Indices(layout, name), value2, out num3);
					if (j >= 0)
					{
						kMonoBehaviour = comps[j];
					}
				}
				if (kMonoBehaviour == null && gameObject != null)
				{
					SaveLoadRoot component = gameObject.GetComponent<SaveLoadRoot>();
					int index;
					if (component != null && (index = ((List<string>)OptionalField.GetValue(component)).IndexOf(text)) != -1)
					{
						var optional = (List<string>)OptionalField.GetValue(component);
						DebugUtil.DevAssert(value2 == 0 && num3 == 0, $"Implementation does not support multiple components with optional components, type {text}, {value2}, {num3}. Using only the first one and skipping the rest.");
						Type type2 = Type.GetType(optional[index]);
						if (num3 == 0)
						{
							kMonoBehaviour = (KMonoBehaviour)gameObject.AddComponent(type2);
						}
					}
				}
				if (kMonoBehaviour == null)
				{
					reader.SkipBytes(num2);
					continue;
				}
				frame.seen.Set(name.id, num3 + 1);
				if (top)
				{
					long d0 = Stopwatch.GetTimestamp(), b0 = GC.GetTotalMemory(false);
					LookupTicks += d0 - t;
					Deserializer.DeserializeTypeless(kMonoBehaviour, reader);
					long d1 = Stopwatch.GetTimestamp();
					MappingTicks += d1 - d0;
					if (kMonoBehaviour is ISaveLoadableDetails details)
					{
						details.Deserialize(reader);
					}
					t = Stopwatch.GetTimestamp();
					DetailsTicks += t - d1;
					Charge(text, t - d0, GC.GetTotalMemory(false) - b0);
				}
				else
				{
					Deserializer.DeserializeTypeless(kMonoBehaviour, reader);
					if (kMonoBehaviour is ISaveLoadableDetails details)
					{
						details.Deserialize(reader);
					}
				}
				if (reader.Position != position + num2)
				{
					DebugUtil.LogWarningArgs("Expected to be at offset", position + num2, "but was only at offset", reader.Position, ". Skipping to catch up.");
					reader.SkipBytes(position + num2 - reader.Position);
				}
			}
			if (top)
			{
				LookupTicks += Stopwatch.GetTimestamp() - t;
			}
			comps.Clear();
		}

		/// <summary>Per top-level component: calls, ticks, and allocated bytes.
		///
		/// The bytes are a GC.GetTotalMemory delta, so they are an allocation count ONLY while
		/// collections are off (the collector hold); without it a delta spans collections and
		/// means nothing. AsyncLoad prints the column as unavailable rather than wrong in that case.
		/// Two extra GetTotalMemory calls per top-level component: AsyncLoad measured that call at
		/// 0.03 us over 955,732 of them, so at ~2.5 M components here this costs well under 0.2 s,
		/// and it lands inside the deserialize stamp it is describing.</summary>
		private static void Charge(string component, long ticks, long bytes)
		{
			if (!ByComponent.TryGetValue(component, out long[] c))
			{
				ByComponent[component] = c = new long[3];
			}
			c[0]++;
			c[1] += ticks;
			c[2] += bytes;
		}
	}
}
