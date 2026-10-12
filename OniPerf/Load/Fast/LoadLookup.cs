using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;

namespace OniPerf
{
	/// <summary>
	/// The two lookups SaveLoadRoot.LoadInternal does per saved component, made per-name and
	/// per-layout instead of per-entry. No Unity types here, so the offline harness links it.
	///
	/// Klei, per saved component: ReadKleiString (a UTF-8 decode and a new string),
	/// serializableComponentManagers.TryGetValue, a Dictionary&lt;string,int&gt; for the occurrence
	/// count, then a scan of the object's GetComponents array doing GetType + sTypeToString lookup +
	/// string compare on every component until the wanted occurrence of the name.
	///
	/// Here:
	///   Names    the saved name bytes are hashed and matched against an intern table, so each
	///            distinct name is decoded once per load; the table entry carries a dense id and the
	///            component manager lookup result.
	///   Layouts  an object's component Type sequence (instances of one prefab share it) is looked
	///            up by identity hash; the layout holds, per name id, the ascending indices of the
	///            components whose type string equals the name, built with Klei's own
	///            type-to-string map, so "the value2-th match" is idx[value2] and "no match" leaves
	///            Klei's num3 at idx.Length.
	/// </summary>
	internal static class LoadLookup
	{
		internal sealed class Name
		{
			public byte[] bytes;
			public int hash;
			public string text;
			public int id;
			public object manager;
			public Name next;
		}

		internal sealed class Layout
		{
			public Type[] types;
			public int hash;
			public int[][] byId = new int[16][];
			public Dictionary<string, int[]> byName;
			public Layout next;
		}

		private static readonly int[] None = new int[0];
		private static Name[] names = new Name[4096];
		private static int nameCount;
		private static Layout[] layouts = new Layout[1024];
		private static int layoutCount;
		public static long NameHits, NameMisses, LayoutHits, LayoutMisses;

		public static int NameCount => nameCount;
		public static int LayoutCount => layoutCount;

		public static void Reset()
		{
			Array.Clear(names, 0, names.Length);
			Array.Clear(layouts, 0, layouts.Length);
			nameCount = layoutCount = 0;
			NameHits = NameMisses = LayoutHits = LayoutMisses = 0;
		}

		/// <summary>The name at b[pos] (int length, then UTF-8 bytes), or null for a negative length.
		/// <paramref name="consumed"/> is the byte count ReadKleiString would advance. A new name gets
		/// its text decoded exactly as ReadKleiString does and <paramref name="resolve"/> run once.</summary>
		public static Name ReadName(byte[] b, int pos, out int consumed, Func<string, object> resolve)
		{
			int len = BitConverter.ToInt32(b, pos);
			if (len < 0)
			{
				consumed = 4;
				return null;
			}
			consumed = 4 + len;
			int start = pos + 4;
			uint h = 2166136261;
			for (int i = 0; i < len; i++)
			{
				h = (h ^ b[start + i]) * 16777619;
			}
			int hash = (int)h;
			int slot = hash & (names.Length - 1);
			for (Name n = names[slot]; n != null; n = n.next)
			{
				if (n.hash == hash && Same(n.bytes, b, start, len))
				{
					NameHits++;
					return n;
				}
			}
			NameMisses++;
			byte[] copy = new byte[len];
			Buffer.BlockCopy(b, start, copy, 0, len);
			var name = new Name { bytes = copy, hash = hash, text = Encoding.UTF8.GetString(b, start, len), id = nameCount++ };
			name.manager = resolve?.Invoke(name.text);
			name.next = names[slot];
			names[slot] = name;
			if (nameCount > names.Length)
			{
				Grow();
			}
			return name;
		}

		private static void Grow()
		{
			var old = names;
			names = new Name[old.Length * 2];
			foreach (Name head in old)
			{
				for (Name n = head; n != null;)
				{
					Name next = n.next;
					int slot = n.hash & (names.Length - 1);
					n.next = names[slot];
					names[slot] = n;
					n = next;
				}
			}
		}

		private static bool Same(byte[] want, byte[] b, int start, int len)
		{
			if (want.Length != len)
			{
				return false;
			}
			for (int i = 0; i < len; i++)
			{
				if (want[i] != b[start + i])
				{
					return false;
				}
			}
			return true;
		}

		/// <summary>The layout for these component types (the first <paramref name="count"/> of
		/// <paramref name="types"/>), built on first sight. <paramref name="typeToString"/> is
		/// SaveLoadRoot.sTypeToString, filled for every type exactly as Klei's scan would fill it.</summary>
		public static Layout GetLayout(Type[] types, int count, Dictionary<Type, string> typeToString)
		{
			int hash = count;
			for (int i = 0; i < count; i++)
			{
				hash = hash * 31 + RuntimeHelpers.GetHashCode(types[i]);
			}
			int slot = hash & (layouts.Length - 1);
			for (Layout l = layouts[slot]; l != null; l = l.next)
			{
				if (l.hash == hash && l.types.Length == count && SameTypes(l.types, types, count))
				{
					LayoutHits++;
					return l;
				}
			}
			LayoutMisses++;
			var layout = new Layout { types = new Type[count], hash = hash, byName = new Dictionary<string, int[]>() };
			Array.Copy(types, layout.types, count);
			var build = new Dictionary<string, List<int>>();
			for (int i = 0; i < count; i++)
			{
				Type type = types[i];
				if (!typeToString.TryGetValue(type, out string s))
				{
					s = type.ToString();
					typeToString[type] = s;
				}
				if (!build.TryGetValue(s, out List<int> list))
				{
					build[s] = list = new List<int>();
				}
				list.Add(i);
			}
			foreach (var kv in build)
			{
				layout.byName[kv.Key] = kv.Value.ToArray();
			}
			layout.next = layouts[slot];
			layouts[slot] = layout;
			if (++layoutCount > layouts.Length)
			{
				GrowLayouts();
			}
			return layout;
		}

		private static void GrowLayouts()
		{
			var old = layouts;
			layouts = new Layout[old.Length * 2];
			foreach (Layout head in old)
			{
				for (Layout l = head; l != null;)
				{
					Layout next = l.next;
					int slot = l.hash & (layouts.Length - 1);
					l.next = layouts[slot];
					layouts[slot] = l;
					l = next;
				}
			}
		}

		private static bool SameTypes(Type[] a, Type[] b, int count)
		{
			for (int i = 0; i < count; i++)
			{
				if ((object)a[i] != b[i])
				{
					return false;
				}
			}
			return true;
		}

		/// <summary>Ascending indices of the layout's components whose type string is this name;
		/// empty when there are none.</summary>
		public static int[] Indices(Layout layout, Name name)
		{
			int id = name.id;
			if (id >= layout.byId.Length)
			{
				Array.Resize(ref layout.byId, Math.Max(layout.byId.Length * 2, id + 1));
			}
			int[] idx = layout.byId[id];
			if (idx == null)
			{
				if (!layout.byName.TryGetValue(name.text, out idx))
				{
					idx = None;
				}
				layout.byId[id] = idx;
			}
			return idx;
		}

		/// <summary>Klei's scan result from the indices: the component index for occurrence
		/// <paramref name="wanted"/> (or -1), and Klei's final num3.</summary>
		public static int Pick(int[] idx, int wanted, out int num3)
		{
			if (wanted < idx.Length)
			{
				num3 = wanted;
				return idx[wanted];
			}
			num3 = idx.Length;
			return -1;
		}

		/// <summary>Per-object occurrence counts by name id (Klei's per-call Dictionary&lt;string,int&gt;),
		/// cleared in O(1) by bumping a stamp. One per LoadInternal nesting depth.</summary>
		internal sealed class Seen
		{
			private int[] count = new int[64];
			private int[] stamp = new int[64];
			private int now = 1;

			public void Clear()
			{
				if (++now == int.MaxValue)
				{
					Array.Clear(stamp, 0, stamp.Length);
					now = 1;
				}
			}

			public int Get(int id)
			{
				return id < stamp.Length && stamp[id] == now ? count[id] : 0;
			}

			public void Set(int id, int value)
			{
				if (id >= stamp.Length)
				{
					int n = Math.Max(stamp.Length * 2, id + 1);
					Array.Resize(ref stamp, n);
					Array.Resize(ref count, n);
				}
				stamp[id] = now;
				count[id] = value;
			}
		}
	}
}
