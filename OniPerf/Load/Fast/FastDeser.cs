// Adapted from the game's own code and NOT covered by the MIT licence: see NOTICE.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using KSerialization;
using UnityEngine;

namespace OniPerf
{
	/// <summary>
	/// Compiled replacement for KSerialization.DeserializationMapping.Deserialize (decomp
	/// DeserializationMapping.cs 82-160). Klei's version does, per field of every component of every
	/// object: FieldInfo.GetValue, ReadValue (a switch that boxes every primitive), FieldInfo.SetValue,
	/// and MethodInfo.Invoke for the three callbacks.
	///
	/// Per mapping this builds a plan once, from the mapping's own resolved members, so the set of
	/// fields, their order and the skip rules are Klei's:
	///   - primitive, string, vector, colour and enum fields: an IL-emitted step that reads the
	///     value and stores it straight into the field (Boolean is ReadByte()==1, enums are
	///     ReadInt32 converted to the underlying type, as Enum.ToObject(type, int) does);
	///   - every other valid field: Klei's own ReadValue, with a compiled getter and setter in place
	///     of FieldInfo.GetValue/SetValue (null into a value-type field stores default, as
	///     SetValue does);
	///   - properties: Klei's reflection path, unchanged;
	///   - members that no longer exist: Klei's skip rules, copied.
	/// Callbacks on reference types are compiled calls; on value types they stay MethodInfo.Invoke
	/// (it runs on the box, which is what the caller holds).
	/// Only error text can differ: a callback that throws is no longer wrapped in
	/// TargetInvocationException before Deserializer's own wrap.
	/// </summary>
	internal static class FastDeser
	{
		private static readonly FieldInfo TemplateField = AccessTools.Field(typeof(DeserializationMapping), "template");
		private static readonly FieldInfo InfoListField = AccessTools.Field(typeof(DeserializationMapping), "deserializationInfo");
		private static readonly Func<DeserializationMapping, KSerialization.TypeInfo, IReader, object, object> ReadValue =
			(Func<DeserializationMapping, KSerialization.TypeInfo, IReader, object, object>)Delegate.CreateDelegate(
				typeof(Func<DeserializationMapping, KSerialization.TypeInfo, IReader, object, object>),
				AccessTools.Method(typeof(DeserializationMapping), "ReadValue"));

		private static readonly Dictionary<DeserializationMapping, Plan> plans = new Dictionary<DeserializationMapping, Plan>();

		// Counters for the load report. Callback time is summed at every depth.
		public static long CallbackTicks, Runs, FastSteps, GenericSteps, CompositeSteps, PropertySteps, SkipSteps;
		public static int PlansBuilt, FastFields, GenericFields, CompositeFields;
		// Time inside outermost mappings (nested ones are included in their parent).
		public static long OuterTicks;
		[ThreadStatic] private static int depth;
		// Deserialize-ahead: set on the worker thread, where nested reads use the mapping and plan
		// resolved on the main thread (OffMainPure) and nothing touches the shared tables or counters.
		[ThreadStatic] private static bool offMain;
		public static readonly Dictionary<string, long> PropertyRuns = new Dictionary<string, long>();

		public static void Install(Harmony harmony)
		{
			harmony.Patch(AccessTools.Method(typeof(DeserializationMapping), "Deserialize", new[] { typeof(object), typeof(IReader) }),
				prefix: new HarmonyMethod(typeof(FastDeser), nameof(DeserializePrefix)));
		}

		public static void ResetCounters()
		{
			CallbackTicks = Runs = FastSteps = GenericSteps = CompositeSteps = PropertySteps = SkipSteps = OuterTicks = 0;
			PropertyRuns.Clear();
		}

		private static bool DeserializePrefix(DeserializationMapping __instance, object obj, IReader reader, ref bool __result)
		{
			Deserialize(__instance, obj, reader);
			__result = true;
			return false;
		}

		/// <summary>What the prefix runs. Nested values call it directly (no Harmony trampoline), and the
		/// offline harness (bench/deserharness) calls it with no patch installed.</summary>
		public static void Deserialize(DeserializationMapping __instance, object obj, IReader reader)
		{
			if (obj == null)
			{
				throw new ArgumentException("obj cannot be null");
			}
			if (!plans.TryGetValue(__instance, out Plan plan))
			{
				plan = new Plan(__instance, obj.GetType());
				plans[__instance] = plan;
				PlansBuilt++;
			}
			if (depth++ == 0)
			{
				long t = Stopwatch.GetTimestamp();
				try
				{
					plan.Run(__instance, obj, reader);
				}
				finally
				{
					depth--;
					OuterTicks += Stopwatch.GetTimestamp() - t;
				}
			}
			else
			{
				try
				{
					plan.Run(__instance, obj, reader);
				}
				finally
				{
					depth--;
				}
			}
		}

		// ---- deserialize-ahead: the part of a mapping that can run off the main thread ----

		/// <summary>Main thread. A handle (the plan) when <paramref name="type"/>'s mapping can run
		/// on the worker: no onDeserializing, custom deserialize, property or generic (Klei
		/// ReadValue) step, and every nested value the same with no callbacks at all. Its own
		/// onDeserialized is allowed; the main thread runs it later (RunOnDeserialized). Null when not.</summary>
		internal static object OffMainPure(DeserializationMapping mapping, Type type)
		{
			if (!plans.TryGetValue(mapping, out Plan plan))
			{
				plan = new Plan(mapping, type);
				plans[mapping] = plan;
				PlansBuilt++;
			}
			return !type.IsValueType && PlanPure(plan, true) ? plan : null;
		}

		/// <summary>Worker thread: the field steps of a plan OffMainPure returned.</summary>
		internal static void RunOffMain(object handle, DeserializationMapping mapping, object obj, IReader reader)
		{
			offMain = true;
			try
			{
				((Plan)handle).RunFields(mapping, obj, reader);
			}
			finally
			{
				offMain = false;
			}
		}

		/// <summary>Main thread, after the worker: the plan's onDeserialized, if it has one.</summary>
		internal static void RunOnDeserialized(object handle, object obj) => ((Plan)handle).RunOnDeserialized(obj);

		private static bool Resolve(Type type, out DeserializationMapping m, out Plan p)
		{
			m = null;
			p = null;
			try
			{
				m = Manager.GetDeserializationMapping(type);
			}
			catch (Exception)
			{
				return false;
			}
			if (!plans.TryGetValue(m, out p))
			{
				p = new Plan(m, type);
				plans[m] = p;
				PlansBuilt++;
			}
			return true;
		}

		private static bool PlanPure(Plan p, bool top)
		{
			if (p.pure == 0)
			{
				// Provisional, so a type that contains itself is judged not pure rather than looping.
				p.pure = 2;
				bool ok = p.onDeserializing == null && p.customDeserialize == null;
				for (int i = 0; ok && i < p.steps.Length; i++)
				{
					Step s = p.steps[i];
					ok = s.kind == Kind.Fast || s.kind == Kind.Skip || (s.kind == Kind.Composite && ReaderPure(s.read));
				}
				p.pure = ok ? 1 : 2;
			}
			return p.pure == 1 && (top || p.onDeserialized == null);
		}

		private static bool ReaderPure(Func<DeserializationMapping, IReader, object, object> read)
		{
			switch (read.Target)
			{
				case UserDefinedReader u:
					if (u.p == null && !Resolve(u.type, out u.m, out u.p))
					{
						return false;
					}
					return PlanPure(u.p, false);
				case SeqReader q:
					return q.Pure();
				default:
					return false;
			}
		}

		private enum Kind { Fast, Composite, Generic, Property, Skip }

		private sealed class Step
		{
			public Kind kind;
			public FieldInfo field;
			public PropertyInfo property;
			public string propertyName;
			public KSerialization.TypeInfo typeInfo;
			public Action<object, IReader> fast;
			public Func<object, object> get;
			public Action<object, object> set;
			public Func<DeserializationMapping, IReader, object, object> read;
		}

		private sealed class Plan
		{
			internal readonly Step[] steps;
			private readonly Type objType;
			internal readonly MethodInfo onDeserializing, onDeserialized, customDeserialize;
			// OffMainPure's verdict: 0 not yet asked, 1 pure, 2 not.
			internal int pure;
			private readonly Action<object> onDeserializingFn, onDeserializedFn;
			private readonly Action<object, IReader> customDeserializeFn;

			public Plan(DeserializationMapping mapping, Type type)
			{
				objType = type;
				var template = (DeserializationTemplate)TemplateField.GetValue(mapping);
				onDeserializing = template.onDeserializing;
				onDeserialized = template.onDeserialized;
				customDeserialize = template.customDeserialize;
				if (!type.IsValueType)
				{
					onDeserializingFn = CompileCallback(onDeserializing);
					onDeserializedFn = CompileCallback(onDeserialized);
					customDeserializeFn = CompileCustom(customDeserialize);
				}
				var list = (IList)InfoListField.GetValue(mapping);
				steps = new Step[list.Count];
				FieldInfo fValid = null, fField = null, fProperty = null, fType = null;
				for (int i = 0; i < list.Count; i++)
				{
					object info = list[i];
					if (fValid == null)
					{
						Type t = info.GetType();
						fValid = AccessTools.Field(t, "valid");
						fField = AccessTools.Field(t, "field");
						fProperty = AccessTools.Field(t, "property");
						fType = AccessTools.Field(t, "typeInfo");
					}
					var step = new Step
					{
						field = (FieldInfo)fField.GetValue(info),
						property = (PropertyInfo)fProperty.GetValue(info),
						typeInfo = (KSerialization.TypeInfo)fType.GetValue(info),
					};
					if (!(bool)fValid.GetValue(info))
					{
						step.kind = Kind.Skip;
					}
					else if (step.field != null)
					{
						step.fast = CompileFast(step.field, step.typeInfo.info & SerializationTypeInfo.VALUE_MASK);
						if (step.fast != null)
						{
							step.kind = Kind.Fast;
							FastFields++;
						}
						else
						{
							step.get = CompileGetter(step.field);
							step.set = CompileSetter(step.field);
							step.read = CompileComposite(step.typeInfo);
							if (step.read != null)
							{
								step.kind = Kind.Composite;
								CompositeFields++;
							}
							else
							{
								step.kind = Kind.Generic;
								GenericFields++;
							}
						}
					}
					else if (step.property != null)
					{
						step.kind = Kind.Property;
						step.propertyName = type.Name + "." + step.property.Name;
					}
					else
					{
						throw new Exception("????");
					}
					steps[i] = step;
				}
			}

			/// <summary>The worker's run: field steps only. The caller has checked OffMainPure, so
			/// there is no onDeserializing, custom deserialize, property or generic step, and
			/// onDeserialized (if any) is left for the main thread's RunOnDeserialized.</summary>
			public void RunFields(DeserializationMapping mapping, object obj, IReader reader)
			{
				for (int i = 0; i < steps.Length; i++)
				{
					Step s = steps[i];
					switch (s.kind)
					{
						case Kind.Fast:
							try
							{
								s.fast(obj, reader);
							}
							catch (Exception ex)
							{
								throw FieldError(s.field, obj, ex);
							}
							break;
						case Kind.Composite:
							try
							{
								object value = s.get(obj);
								s.set(obj, s.read(mapping, reader, value));
							}
							catch (Exception ex)
							{
								throw FieldError(s.field, obj, ex);
							}
							break;
						case Kind.Skip:
							Skip(s.typeInfo.info & SerializationTypeInfo.VALUE_MASK, reader);
							break;
						default:
							throw new InvalidOperationException("deser-ahead: a " + s.kind + " step in a plan judged pure");
					}
				}
			}

			public void RunOnDeserialized(object obj)
			{
				if (onDeserialized != null)
				{
					long t = Stopwatch.GetTimestamp();
					if (onDeserializedFn != null) onDeserializedFn(obj); else onDeserialized.Invoke(obj, null);
					CallbackTicks += Stopwatch.GetTimestamp() - t;
				}
			}

			public void Run(DeserializationMapping mapping, object obj, IReader reader)
			{
				Runs++;
				if (onDeserializing != null)
				{
					long t = Stopwatch.GetTimestamp();
					if (onDeserializingFn != null) onDeserializingFn(obj); else onDeserializing.Invoke(obj, null);
					CallbackTicks += Stopwatch.GetTimestamp() - t;
				}
				for (int i = 0; i < steps.Length; i++)
				{
					Step s = steps[i];
					switch (s.kind)
					{
						case Kind.Fast:
							FastSteps++;
							try
							{
								s.fast(obj, reader);
							}
							catch (Exception ex)
							{
								throw FieldError(s.field, obj, ex);
							}
							break;
						case Kind.Composite:
							CompositeSteps++;
							try
							{
								object value = s.get(obj);
								s.set(obj, s.read(mapping, reader, value));
							}
							catch (Exception ex)
							{
								throw FieldError(s.field, obj, ex);
							}
							break;
						case Kind.Generic:
							GenericSteps++;
							try
							{
								object value = s.get(obj);
								s.set(obj, ReadValue(mapping, s.typeInfo, reader, value));
							}
							catch (Exception ex)
							{
								throw FieldError(s.field, obj, ex);
							}
							break;
						case Kind.Property:
							PropertySteps++;
							PropertyRuns.TryGetValue(s.propertyName, out long pr);
							PropertyRuns[s.propertyName] = pr + 1;
							try
							{
								object value = s.property.GetValue(obj, null);
								s.property.SetValue(obj, ReadValue(mapping, s.typeInfo, reader, value), null);
							}
							catch (Exception ex)
							{
								string text = $"Exception occurred while attempting to deserialize property {s.property}({s.property.PropertyType}) on object {obj}({obj.GetType()}).\n{ex.ToString()}";
								UnityEngine.Debug.LogError(text);
								throw new Exception(text, ex);
							}
							break;
						default:
							SkipSteps++;
							Skip(s.typeInfo.info & SerializationTypeInfo.VALUE_MASK, reader);
							break;
					}
				}
				if (customDeserialize != null)
				{
					long t = Stopwatch.GetTimestamp();
					if (customDeserializeFn != null) customDeserializeFn(obj, reader); else customDeserialize.Invoke(obj, new object[] { reader });
					CallbackTicks += Stopwatch.GetTimestamp() - t;
				}
				if (onDeserialized != null)
				{
					long t = Stopwatch.GetTimestamp();
					if (onDeserializedFn != null) onDeserializedFn(obj); else onDeserialized.Invoke(obj, null);
					CallbackTicks += Stopwatch.GetTimestamp() - t;
				}
			}
		}

		// ---- collections and nested user-defined values ----

		private static readonly Action<DeserializationMapping, Array, KSerialization.TypeInfo, IReader> ReadArrayFast =
			(Action<DeserializationMapping, Array, KSerialization.TypeInfo, IReader>)Delegate.CreateDelegate(
				typeof(Action<DeserializationMapping, Array, KSerialization.TypeInfo, IReader>),
				AccessTools.Method(typeof(DeserializationMapping), "ReadArrayFast"));

		/// <summary>
		/// ReadValue's UserDefined, Array, List, HashSet and Queue cases (the game's DeserializationMapping.cs
		/// 182-281, 322-364) with the per-call reflection taken out: Klei looks up the parameterless
		/// constructor on every nested value, fills an Array through Array.SetValue (boxing each element)
		/// and builds the collection with Activator.CreateInstance(type, array), which binds the
		/// constructor on every call. Here the element array is typed, the constructor is bound once and
		/// called through emitted IL. Element reads, their order and the reused element box are Klei's.
		/// Null when the shape is not one of these (Dictionary, Pair and anything unexpected stay Klei's).
		/// </summary>
		private static Func<DeserializationMapping, IReader, object, object> CompileComposite(KSerialization.TypeInfo ti)
		{
			if (ti == null || ti.type == null)
			{
				return null;
			}
			SerializationTypeInfo kind = ti.info & SerializationTypeInfo.VALUE_MASK;
			switch (kind)
			{
				case SerializationTypeInfo.UserDefined:
					return new UserDefinedReader(ti.type).Read;
				case SerializationTypeInfo.Array:
				case SerializationTypeInfo.List:
				case SerializationTypeInfo.HashSet:
				case SerializationTypeInfo.Queue:
				{
					if (ti.subTypes == null || ti.subTypes.Length < 1 || ti.subTypes[0] == null || ti.subTypes[0].type == null)
					{
						return null;
					}
					Type elem = ti.subTypes[0].type;
					if (elem.ContainsGenericParameters)
					{
						return null;
					}
					var reader = (SeqReader)Activator.CreateInstance(typeof(SeqReader<>).MakeGenericType(elem));
					return reader.Init(ti, kind) ? reader.Read : null;
				}
				default:
					return null;
			}
		}

		private sealed class UserDefinedReader
		{
			internal readonly Type type;
			private readonly bool hasCtor;
			// Resolved on the main thread by OffMainPure, for the worker.
			internal DeserializationMapping m;
			internal Plan p;

			public UserDefinedReader(Type type)
			{
				this.type = type;
				hasCtor = type.GetConstructor(Type.EmptyTypes) != null;
			}

			public object Read(DeserializationMapping mapping, IReader reader, object baseValue)
			{
				if (reader.ReadInt32() < 0)
				{
					return null;
				}
				object obj = baseValue ?? (hasCtor ? Activator.CreateInstance(type) : System.Runtime.Serialization.FormatterServices.GetUninitializedObject(type));
				if (offMain)
				{
					p.RunFields(m, obj, reader);
				}
				else
				{
					Deserialize(Manager.GetDeserializationMapping(type), obj, reader);
				}
				return obj;
			}
		}

		private abstract class SeqReader
		{
			public abstract bool Init(KSerialization.TypeInfo ti, SerializationTypeInfo kind);
			/// <summary>Main thread: whether Read can run on the worker, resolving what it needs.</summary>
			public abstract bool Pure();
			public abstract object Read(DeserializationMapping mapping, IReader reader, object baseValue);
		}

		private sealed class SeqReader<T> : SeqReader
		{
			private KSerialization.TypeInfo elem;
			private bool pod, valueType, isArray;
			private Func<T[], object> make;
			// Reference-type elements that are themselves user-defined or collections: the same
			// compiled readers, so a List<ReportEntry> does not go back through ReadValue per element.
			private Func<DeserializationMapping, IReader, object, object> elemRead;
			private DeserializationMapping vm;
			private Plan vp;

			public override bool Pure()
			{
				if (pod)
				{
					return true;
				}
				if (valueType)
				{
					if (vp == null && !Resolve(elem.type, out vm, out vp))
					{
						return false;
					}
					return PlanPure(vp, false);
				}
				return elemRead != null && ReaderPure(elemRead);
			}

			public override bool Init(KSerialization.TypeInfo ti, SerializationTypeInfo kind)
			{
				elem = ti.subTypes[0];
				// HashSet has no ReadArrayFast branch in Klei's ReadValue.
				pod = kind != SerializationTypeInfo.HashSet && Helper.IsPOD(elem.info);
				valueType = Helper.IsValueType(elem.info);
				if (!pod && !valueType)
				{
					elemRead = CompileComposite(elem);
				}
				if (kind == SerializationTypeInfo.Array)
				{
					isArray = true;
					return ti.type == typeof(T[]);
				}
				Type target = ti.genericInstantiationType;
				if (target == null)
				{
					return false;
				}
				// The constructor Activator.CreateInstance(target, T[]) binds to: the single one-argument
				// constructor that accepts a T[] (IEnumerable<T> for List, HashSet and Queue).
				ConstructorInfo ctor = null;
				foreach (ConstructorInfo c in target.GetConstructors())
				{
					ParameterInfo[] ps = c.GetParameters();
					if (ps.Length == 1 && ps[0].ParameterType.IsAssignableFrom(typeof(T[])))
					{
						if (ctor != null)
						{
							return false;
						}
						ctor = c;
					}
				}
				if (ctor == null)
				{
					return false;
				}
				var dm = new DynamicMethod("FastDeserNew_" + target.Name, typeof(object), new[] { typeof(T[]) }, typeof(FastDeser).Module, true);
				ILGenerator il = dm.GetILGenerator();
				il.Emit(OpCodes.Ldarg_0);
				il.Emit(OpCodes.Newobj, ctor);
				if (target.IsValueType)
				{
					il.Emit(OpCodes.Box, target);
				}
				il.Emit(OpCodes.Ret);
				make = (Func<T[], object>)dm.CreateDelegate(typeof(Func<T[], object>));
				return true;
			}

			public override object Read(DeserializationMapping mapping, IReader reader, object baseValue)
			{
				reader.ReadInt32();
				int n = reader.ReadInt32();
				if (n < 0)
				{
					return null;
				}
				var array = new T[n];
				if (pod)
				{
					ReadArrayFast(mapping, array, elem, reader);
				}
				else if (valueType)
				{
					DeserializationMapping m = offMain ? vm : Manager.GetDeserializationMapping(elem.type);
					object box = Activator.CreateInstance(elem.type);
					for (int i = 0; i < n; i++)
					{
						if (offMain) vp.RunFields(m, box, reader); else Deserialize(m, box, reader);
						array[i] = (T)box;
					}
				}
				else
				{
					for (int i = 0; i < n; i++)
					{
						object v = elemRead != null ? elemRead(mapping, reader, null) : ReadValue(mapping, elem, reader, null);
						// Array.SetValue stores default for null into a value-type element.
						array[i] = v == null ? default(T) : (T)v;
					}
				}
				return isArray ? array : make(array);
			}
		}

		private static Exception FieldError(FieldInfo field, object obj, Exception ex)
		{
			string text = $"Exception occurred while attempting to deserialize field {field}({field.FieldType}) on object {obj}({obj.GetType()}).\n{ex.ToString()}";
			UnityEngine.Debug.LogError(text);
			return new Exception(text, ex);
		}

		/// <summary>The skip half of Klei's Deserialize loop plus SkipValue, copied.</summary>
		private static void Skip(SerializationTypeInfo kind, IReader reader)
		{
			switch (kind)
			{
				case SerializationTypeInfo.Array:
				case SerializationTypeInfo.Dictionary:
				case SerializationTypeInfo.List:
				case SerializationTypeInfo.HashSet:
				case SerializationTypeInfo.Queue:
				{
					int length = reader.ReadInt32();
					if (reader.ReadInt32() > -1)
					{
						reader.SkipBytes(length);
					}
					break;
				}
				case SerializationTypeInfo.UserDefined:
				case SerializationTypeInfo.Pair:
				{
					int num = reader.ReadInt32();
					if (num > 0)
					{
						reader.SkipBytes(num);
					}
					break;
				}
				case SerializationTypeInfo.SByte:
				case SerializationTypeInfo.Byte:
				case SerializationTypeInfo.Boolean:
					reader.SkipBytes(1);
					break;
				case SerializationTypeInfo.Int16:
				case SerializationTypeInfo.UInt16:
					reader.SkipBytes(2);
					break;
				case SerializationTypeInfo.Int32:
				case SerializationTypeInfo.UInt32:
				case SerializationTypeInfo.Single:
				case SerializationTypeInfo.Enumeration:
					reader.SkipBytes(4);
					break;
				case SerializationTypeInfo.Int64:
				case SerializationTypeInfo.UInt64:
				case SerializationTypeInfo.Double:
				case SerializationTypeInfo.Vector2I:
				case SerializationTypeInfo.Vector2:
					reader.SkipBytes(8);
					break;
				case SerializationTypeInfo.String:
				{
					int num = reader.ReadInt32();
					if (num > 0)
					{
						reader.SkipBytes(num);
					}
					break;
				}
				case SerializationTypeInfo.Vector3:
					reader.SkipBytes(12);
					break;
				case SerializationTypeInfo.Colour:
					reader.SkipBytes(4);
					break;
				default:
					throw new ArgumentException("Unhandled type. Not sure how to skip by");
			}
		}

		// ---- IL emission ----

		private static readonly Type[] ObjReader = { typeof(object), typeof(IReader) };

		private static MethodInfo R(string name) => typeof(IReader).GetMethod(name);
		private static MethodInfo H(string name) => typeof(IOHelper).GetMethod(name, BindingFlags.Public | BindingFlags.Static);

		private static bool Emittable(FieldInfo f)
		{
			Type d = f.DeclaringType;
			return !f.IsStatic && !f.IsInitOnly && !f.IsLiteral && d != null && !d.ContainsGenericParameters;
		}

		private static void LoadTarget(ILGenerator il, Type declaring)
		{
			il.Emit(OpCodes.Ldarg_0);
			il.Emit(declaring.IsValueType ? OpCodes.Unbox : OpCodes.Castclass, declaring);
		}

		/// <summary>Reader call and expected field type for each primitive kind, as ReadValue.</summary>
		private static Action<object, IReader> CompileFast(FieldInfo f, SerializationTypeInfo kind)
		{
			if (!Emittable(f))
			{
				return null;
			}
			Type ft = f.FieldType;
			MethodInfo read;
			Type expect;
			switch (kind)
			{
				case SerializationTypeInfo.SByte: read = R("ReadSByte"); expect = typeof(sbyte); break;
				case SerializationTypeInfo.Byte: read = R("ReadByte"); expect = typeof(byte); break;
				case SerializationTypeInfo.Boolean: read = R("ReadByte"); expect = typeof(bool); break;
				case SerializationTypeInfo.Int16: read = R("ReadInt16"); expect = typeof(short); break;
				case SerializationTypeInfo.UInt16: read = R("ReadUInt16"); expect = typeof(ushort); break;
				case SerializationTypeInfo.Int32: read = R("ReadInt32"); expect = typeof(int); break;
				case SerializationTypeInfo.UInt32: read = R("ReadUInt32"); expect = typeof(uint); break;
				case SerializationTypeInfo.Int64: read = R("ReadInt64"); expect = typeof(long); break;
				case SerializationTypeInfo.UInt64: read = R("ReadUInt64"); expect = typeof(ulong); break;
				case SerializationTypeInfo.Single: read = R("ReadSingle"); expect = typeof(float); break;
				case SerializationTypeInfo.Double: read = R("ReadDouble"); expect = typeof(double); break;
				case SerializationTypeInfo.String: read = R("ReadKleiString"); expect = typeof(string); break;
				case SerializationTypeInfo.Vector2I: read = H("ReadVector2I"); expect = typeof(Vector2I); break;
				case SerializationTypeInfo.Vector2: read = H("ReadVector2"); expect = typeof(Vector2); break;
				case SerializationTypeInfo.Vector3: read = H("ReadVector3"); expect = typeof(Vector3); break;
				case SerializationTypeInfo.Colour: read = H("ReadColour"); expect = typeof(Color); break;
				case SerializationTypeInfo.Enumeration: read = R("ReadInt32"); expect = ft.IsEnum ? ft : null; break;
				default: return null;
			}
			if (read == null || expect == null || ft != expect)
			{
				return null;
			}
			var dm = new DynamicMethod("FastDeser_" + f.DeclaringType.Name + "_" + f.Name, null, ObjReader, typeof(FastDeser).Module, true);
			ILGenerator il = dm.GetILGenerator();
			LoadTarget(il, f.DeclaringType);
			il.Emit(OpCodes.Ldarg_1);
			il.Emit(read.IsStatic ? OpCodes.Call : OpCodes.Callvirt, read);
			if (kind == SerializationTypeInfo.Boolean)
			{
				il.Emit(OpCodes.Ldc_I4_1);
				il.Emit(OpCodes.Ceq);
			}
			else if (kind == SerializationTypeInfo.Enumeration)
			{
				// Enum.ToObject(type, int) widens the int to long and keeps the low bits.
				switch (Type.GetTypeCode(Enum.GetUnderlyingType(ft)))
				{
					case TypeCode.SByte: il.Emit(OpCodes.Conv_I1); break;
					case TypeCode.Byte: il.Emit(OpCodes.Conv_U1); break;
					case TypeCode.Int16: il.Emit(OpCodes.Conv_I2); break;
					case TypeCode.UInt16: il.Emit(OpCodes.Conv_U2); break;
					case TypeCode.Int32:
					case TypeCode.UInt32: break;
					case TypeCode.Int64:
					case TypeCode.UInt64: il.Emit(OpCodes.Conv_I8); break;
					default: return null;
				}
			}
			il.Emit(OpCodes.Stfld, f);
			il.Emit(OpCodes.Ret);
			return (Action<object, IReader>)dm.CreateDelegate(typeof(Action<object, IReader>));
		}

		private static Func<object, object> CompileGetter(FieldInfo f)
		{
			if (!Emittable(f) && !(f.IsInitOnly && !f.IsStatic))
			{
				return f.GetValue;
			}
			var dm = new DynamicMethod("FastDeserGet_" + f.Name, typeof(object), new[] { typeof(object) }, typeof(FastDeser).Module, true);
			ILGenerator il = dm.GetILGenerator();
			LoadTarget(il, f.DeclaringType);
			il.Emit(OpCodes.Ldfld, f);
			if (f.FieldType.IsValueType)
			{
				il.Emit(OpCodes.Box, f.FieldType);
			}
			il.Emit(OpCodes.Ret);
			return (Func<object, object>)dm.CreateDelegate(typeof(Func<object, object>));
		}

		private static Action<object, object> CompileSetter(FieldInfo f)
		{
			if (!Emittable(f))
			{
				return f.SetValue;
			}
			Type ft = f.FieldType;
			var dm = new DynamicMethod("FastDeserSet_" + f.Name, null, new[] { typeof(object), typeof(object) }, typeof(FastDeser).Module, true);
			ILGenerator il = dm.GetILGenerator();
			if (ft.IsValueType)
			{
				Label notNull = il.DefineLabel();
				il.Emit(OpCodes.Ldarg_1);
				il.Emit(OpCodes.Brtrue_S, notNull);
				LoadTarget(il, f.DeclaringType);
				il.Emit(OpCodes.Ldflda, f);
				il.Emit(OpCodes.Initobj, ft);
				il.Emit(OpCodes.Ret);
				il.MarkLabel(notNull);
				LoadTarget(il, f.DeclaringType);
				il.Emit(OpCodes.Ldarg_1);
				il.Emit(OpCodes.Unbox_Any, ft);
			}
			else
			{
				LoadTarget(il, f.DeclaringType);
				il.Emit(OpCodes.Ldarg_1);
				il.Emit(OpCodes.Castclass, ft);
			}
			il.Emit(OpCodes.Stfld, f);
			il.Emit(OpCodes.Ret);
			return (Action<object, object>)dm.CreateDelegate(typeof(Action<object, object>));
		}

		private static Action<object> CompileCallback(MethodInfo m)
		{
			if (m == null || m.IsStatic || m.GetParameters().Length != 0 || m.DeclaringType.IsValueType || m.DeclaringType.ContainsGenericParameters)
			{
				return null;
			}
			var dm = new DynamicMethod("FastDeserCb_" + m.Name, null, new[] { typeof(object) }, typeof(FastDeser).Module, true);
			ILGenerator il = dm.GetILGenerator();
			il.Emit(OpCodes.Ldarg_0);
			il.Emit(OpCodes.Castclass, m.DeclaringType);
			il.Emit(m.IsVirtual ? OpCodes.Callvirt : OpCodes.Call, m);
			if (m.ReturnType != typeof(void))
			{
				il.Emit(OpCodes.Pop);
			}
			il.Emit(OpCodes.Ret);
			return (Action<object>)dm.CreateDelegate(typeof(Action<object>));
		}

		private static Action<object, IReader> CompileCustom(MethodInfo m)
		{
			if (m == null || m.IsStatic || m.DeclaringType.IsValueType || m.DeclaringType.ContainsGenericParameters)
			{
				return null;
			}
			ParameterInfo[] ps = m.GetParameters();
			if (ps.Length != 1 || ps[0].ParameterType != typeof(IReader))
			{
				return null;
			}
			var dm = new DynamicMethod("FastDeserCustom_" + m.Name, null, ObjReader, typeof(FastDeser).Module, true);
			ILGenerator il = dm.GetILGenerator();
			il.Emit(OpCodes.Ldarg_0);
			il.Emit(OpCodes.Castclass, m.DeclaringType);
			il.Emit(OpCodes.Ldarg_1);
			il.Emit(m.IsVirtual ? OpCodes.Callvirt : OpCodes.Call, m);
			if (m.ReturnType != typeof(void))
			{
				il.Emit(OpCodes.Pop);
			}
			il.Emit(OpCodes.Ret);
			return (Action<object, IReader>)dm.CreateDelegate(typeof(Action<object, IReader>));
		}
	}
}
