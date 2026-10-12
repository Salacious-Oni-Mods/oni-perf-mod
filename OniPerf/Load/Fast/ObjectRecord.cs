using System;
using System.Collections.Generic;
using System.Text;
using KSerialization;
using UnityEngine;

namespace OniPerf
{
	/// <summary>
	/// The whole-object deferral's byte-level half: walk one saved object without building it, and read the one
	/// field a deferred object has to answer for before it exists -- KPrefabID.InstanceID, the key
	/// every Ref&lt;T&gt; and every KPrefabIDTracker.GetInstance caller resolves through.
	///
	/// The record, as SaveLoadRoot.Save writes it (the game's SaveLoadRoot.cs 95-166) and
	/// SaveLoadRoot.Load / LoadInternal read it (174-310):
	///
	///     Vector3 position, Quaternion rotation, Vector3 scale, byte 0     41 bytes
	///     int componentCount
	///     componentCount x { KleiString typeName, int length, length bytes }
	///
	/// Every entry is length-prefixed. The one writer that is not -- the ISerializableComponentManager
	/// loop at SaveLoadRoot.cs 157-165, which writes a key and the manager's own bytes with no length
	/// -- has no implementer in this build (the interface is declared in firstpass and nothing in
	/// either assembly implements it), so serializableComponentManagers is empty and the loop never
	/// writes. savewalk.py and the deserializer harness depend on the same fact and reconcile every
	/// group against its stated byte count; Scan is checked the same way (bench/deserharness --peekid).
	///
	/// The KPrefabID entry is a typeless KSerialization blob: the members of the SAVE's template for
	/// "KPrefabID", in template order (DeserializationMapping.Deserialize, game 85-170). InstanceID
	/// is reached by skipping the members before it with Klei's own skip rules
	/// (DeserializationMapping.Deserialize 133-160 for collections and user types, SkipValue 459-500
	/// for the rest). When every member before it is fixed-size, which is the case the template is
	/// expected to have, the offset is one constant and nothing is skipped per object.
	///
	/// No allocation per object: the type names are compared as UTF-8 bytes against a cached
	/// encoding, the way SmRestore compares state-machine context names.
	/// </summary>
	internal static class ObjectRecord
	{
		public const int HeaderBytes = 41;

		private static readonly byte[] KPrefabIDName = Encoding.UTF8.GetBytes("KPrefabID");

		/// <summary>Members of the save's KPrefabID template that precede InstanceID.</summary>
		private static TypeInfo[] before;
		/// <summary>Byte offset of InstanceID inside the KPrefabID blob when every member before it
		/// is fixed-size, else -1 and the members are skipped per blob.</summary>
		private static int fixedOffset = -1;

		public static bool Ready { get; private set; }
		public static string Why { get; private set; } = "not prepared";

		/// <summary>Build the skip plan from the templates the save just loaded (Manager.DeserializeDirectory
		/// has to have run). False, with Why set, when the plan cannot be made -- the caller then does
		/// not defer whole objects, because a deferred object it cannot key is one no lookup can find.</summary>
		public static bool Prepare()
		{
			Ready = false;
			before = null;
			fixedOffset = -1;
			DeserializationTemplate t = Manager.GetDeserializationTemplate("KPrefabID");
			if (t == null)
			{
				Why = "the save has no KPrefabID template";
				return false;
			}
			var list = new List<TypeInfo>();
			bool found = false;
			foreach (DeserializationTemplate.SerializedInfo m in t.serializedMembers)
			{
				if (m.name == "InstanceID")
				{
					if ((m.typeInfo.info & SerializationTypeInfo.VALUE_MASK) != SerializationTypeInfo.Int32)
					{
						Why = "KPrefabID.InstanceID is saved as " + (m.typeInfo.info & SerializationTypeInfo.VALUE_MASK) + ", not Int32";
						return false;
					}
					found = true;
					break;
				}
				list.Add(m.typeInfo);
			}
			if (!found)
			{
				Why = "the save's KPrefabID template has no InstanceID member";
				return false;
			}
			before = list.ToArray();
			int off = 0;
			foreach (TypeInfo ti in before)
			{
				int n = FixedSize(ti.info & SerializationTypeInfo.VALUE_MASK);
				if (n < 0)
				{
					off = -1;
					break;
				}
				off += n;
			}
			fixedOffset = off;
			Ready = true;
			Why = fixedOffset >= 0
				? "InstanceID at fixed offset " + fixedOffset + " after " + before.Length + " member(s)"
				: "InstanceID after " + before.Length + " member(s), at least one variable-size: skipped per blob";
			return true;
		}

		/// <summary>
		/// Walk the record starting at <paramref name="at"/> (the first byte of its position).
		/// Returns the offset one past its last byte. <paramref name="instanceId"/> is the
		/// KPrefabID's InstanceID, or -1 when the object has no KPrefabID entry (nothing can then
		/// refer to it by id); <paramref name="components"/> is the entry count.
		/// Throws FormatException on a record that runs past <paramref name="limit"/>.
		/// </summary>
		public static int Scan(byte[] b, int at, int limit, out int instanceId, out int components)
		{
			instanceId = -1;
			int i = at + HeaderBytes;
			components = ReadInt(b, ref i, limit);
			if (components < 0)
			{
				throw new FormatException("negative component count " + components + " at " + at);
			}
			for (int c = 0; c < components; c++)
			{
				int nameLen = ReadInt(b, ref i, limit);
				int nameAt = i;
				if (nameLen > 0)
				{
					i += nameLen;
				}
				int len = ReadInt(b, ref i, limit);
				if (len < 0 || i + len > limit)
				{
					throw new FormatException("component " + c + " of the record at " + at + " claims " + len + " bytes past the limit");
				}
				if (instanceId == -1 && nameLen == KPrefabIDName.Length && Same(b, nameAt, KPrefabIDName))
				{
					instanceId = PeekInstanceId(b, i, i + len);
				}
				i += len;
			}
			return i;
		}

		private static readonly byte[] WorldContainerName = Encoding.UTF8.GetBytes("WorldContainer");

		/// <summary>
		/// A WorldContainer record's saved worldOffset and worldSize: the cell rect that
		/// ClusterManager.InitializeWorldGrid stamps into Grid.WorldIdx after the object loop. Members
		/// are walked in the save's template order with the same skip rules as InstanceID. False when
		/// the record has no WorldContainer entry, the save has no template for it, or either member
		/// is missing or not a Vector2I.
		/// </summary>
		public static bool TryReadWorldRect(byte[] b, int at, int limit, out RectInt rect)
		{
			rect = default;
			DeserializationTemplate t = Manager.GetDeserializationTemplate("WorldContainer");
			if (t == null)
			{
				return false;
			}
			int i = at + HeaderBytes;
			int components = ReadInt(b, ref i, limit);
			for (int c = 0; c < components; c++)
			{
				int nameLen = ReadInt(b, ref i, limit);
				int nameAt = i;
				if (nameLen > 0)
				{
					i += nameLen;
				}
				int len = ReadInt(b, ref i, limit);
				if (len < 0 || i + len > limit)
				{
					return false;
				}
				if (nameLen != WorldContainerName.Length || !Same(b, nameAt, WorldContainerName))
				{
					i += len;
					continue;
				}
				int end = i + len;
				bool haveOffset = false, haveSize = false;
				int ox = 0, oy = 0, sx = 0, sy = 0;
				foreach (DeserializationTemplate.SerializedInfo m in t.serializedMembers)
				{
					SerializationTypeInfo ti = m.typeInfo.info & SerializationTypeInfo.VALUE_MASK;
					bool isOffset = m.name == "worldOffset", isSize = m.name == "worldSize";
					if ((isOffset || isSize) && ti == SerializationTypeInfo.Vector2I)
					{
						int x = ReadInt(b, ref i, end);
						int y = ReadInt(b, ref i, end);
						if (isOffset)
						{
							ox = x; oy = y; haveOffset = true;
						}
						else
						{
							sx = x; sy = y; haveSize = true;
						}
						if (haveOffset && haveSize)
						{
							break;
						}
						continue;
					}
					Skip(b, ref i, end, ti);
				}
				if (!haveOffset || !haveSize || sx <= 0 || sy <= 0)
				{
					return false;
				}
				rect = new RectInt(ox, oy, sx, sy);
				return true;
			}
			return false;
		}

		/// <summary>InstanceID out of one KPrefabID blob spanning [at, end).</summary>
		public static int PeekInstanceId(byte[] b, int at, int end)
		{
			if (!Ready)
			{
				throw new InvalidOperationException("ObjectRecord.Prepare has not succeeded: " + Why);
			}
			int i = at;
			if (fixedOffset >= 0)
			{
				i += fixedOffset;
			}
			else
			{
				foreach (TypeInfo ti in before)
				{
					Skip(b, ref i, end, ti.info & SerializationTypeInfo.VALUE_MASK);
				}
			}
			return ReadInt(b, ref i, end);
		}

		private static int FixedSize(SerializationTypeInfo t)
		{
			switch (t)
			{
				case SerializationTypeInfo.SByte:
				case SerializationTypeInfo.Byte:
				case SerializationTypeInfo.Boolean:
					return 1;
				case SerializationTypeInfo.Int16:
				case SerializationTypeInfo.UInt16:
					return 2;
				case SerializationTypeInfo.Int32:
				case SerializationTypeInfo.UInt32:
				case SerializationTypeInfo.Single:
				case SerializationTypeInfo.Enumeration:
				case SerializationTypeInfo.Colour:
					return 4;
				case SerializationTypeInfo.Int64:
				case SerializationTypeInfo.UInt64:
				case SerializationTypeInfo.Double:
				case SerializationTypeInfo.Vector2I:
				case SerializationTypeInfo.Vector2:
					return 8;
				case SerializationTypeInfo.Vector3:
					return 12;
				default:
					return -1;
			}
		}

		/// <summary>Klei's skip, DeserializationMapping.Deserialize 133-160 and SkipValue 459-500.</summary>
		private static void Skip(byte[] b, ref int i, int end, SerializationTypeInfo t)
		{
			switch (t)
			{
				case SerializationTypeInfo.Array:
				case SerializationTypeInfo.Dictionary:
				case SerializationTypeInfo.List:
				case SerializationTypeInfo.HashSet:
				case SerializationTypeInfo.Queue:
				{
					int length = ReadInt(b, ref i, end);
					if (ReadInt(b, ref i, end) > -1)
					{
						i += length;
					}
					break;
				}
				case SerializationTypeInfo.UserDefined:
				case SerializationTypeInfo.Pair:
				case SerializationTypeInfo.String:
				{
					int n = ReadInt(b, ref i, end);
					if (n > 0)
					{
						i += n;
					}
					break;
				}
				default:
				{
					int n = FixedSize(t);
					if (n < 0)
					{
						throw new FormatException("no skip rule for " + t);
					}
					i += n;
					break;
				}
			}
			if (i > end)
			{
				throw new FormatException("skipping " + t + " ran past the blob");
			}
		}

		private static int ReadInt(byte[] b, ref int i, int limit)
		{
			if (i + 4 > limit)
			{
				throw new FormatException("int read at " + i + " runs past " + limit);
			}
			int v = b[i] | b[i + 1] << 8 | b[i + 2] << 16 | b[i + 3] << 24;
			i += 4;
			return v;
		}

		private static bool Same(byte[] b, int at, byte[] name)
		{
			for (int k = 0; k < name.Length; k++)
			{
				if (b[at + k] != name[k])
				{
					return false;
				}
			}
			return true;
		}
	}
}
