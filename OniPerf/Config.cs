using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json.Linq;

// The lever switches, read from `config.json` beside the mod's DLL.
//
// No options screen: an options library is another mod, and this one works on a plain install.
// The file is absent by default and never written. A Workshop update replaces the mod folder, so
// a file written there would be lost on the next update without the player being told. The
// defaults are in the code, the effective set is logged at load, and a player who wants to change
// it creates the file, for example:
//
//   { "brainRate": true, "moveHold": true, "steamPoll": false }
//
// Every key is optional. A key that is missing or the wrong type keeps its default; an unknown key
// is named in the log, so a misspelling is visible. A file that cannot be parsed costs one warning
// and the defaults: a bad config must never stop the mod, and least of all the game.
namespace OniPerf
{
	internal sealed class Config
	{
		/// <summary>Every lever's key and default. Off by default: the two that change gameplay timing,
		/// conveyorPathsCheck, which only checks conveyorPaths against the game's own code, and fastLoad,
		/// the experimental loader, which stays opt-in until its compatibility checks pass.</summary>
		internal static readonly KeyValuePair<string, bool>[] Defaults =
		{
			new KeyValuePair<string, bool>("screenSleep", true),
			new KeyValuePair<string, bool>("propTex", true),
			new KeyValuePair<string, bool>("lerpBlit", true),
			new KeyValuePair<string, bool>("idleSleep", true),
			new KeyValuePair<string, bool>("steamPoll", true),
			new KeyValuePair<string, bool>("pipeCache", true),
			new KeyValuePair<string, bool>("achieveRate", true),
			new KeyValuePair<string, bool>("idleCams", true),
			new KeyValuePair<string, bool>("workerSleep", true),
			new KeyValuePair<string, bool>("raycastCache", true),
			new KeyValuePair<string, bool>("mixRate", true),
			new KeyValuePair<string, bool>("hoverCache", true),
			new KeyValuePair<string, bool>("pausedSkip", true),
			new KeyValuePair<string, bool>("symbolMemo", true),
			new KeyValuePair<string, bool>("overrideRange", true),
			new KeyValuePair<string, bool>("parkAnims", true),
			new KeyValuePair<string, bool>("markSkip", true),
			new KeyValuePair<string, bool>("barRate", true),
			new KeyValuePair<string, bool>("cpuFix", true),
			new KeyValuePair<string, bool>("conveyorPaths", true),
			new KeyValuePair<string, bool>("typeNameCache", true),
			new KeyValuePair<string, bool>("smRestore", true),
			new KeyValuePair<string, bool>("loadGcHold", true),
			new KeyValuePair<string, bool>("conveyorPathsCheck", false),
			new KeyValuePair<string, bool>("fastLoad", false),
			new KeyValuePair<string, bool>("brainRate", false),
			new KeyValuePair<string, bool>("moveHold", false),
		};

		private const float MinHz = 1f, MaxHz = 240f;

		private readonly Dictionary<string, bool> on = new Dictionary<string, bool>();

		/// <summary>Brain updates per second of real time when brainRate is on. The game's own rate is 30.</summary>
		internal float BrainRateHz = 30f;

		/// <summary>Steam callback polls per second when steamPoll is on.</summary>
		internal float SteamPollCallbacksHz = SteamPoll.DefaultCallbacksHz;

		/// <summary>The installed memory, in GB, loadGcHold assumes; 0 reads it from the machine.</summary>
		internal double LoadGcHoldRamGb;

		/// <summary>Where the values came from, for the load line.</summary>
		internal string Source = "defaults (no config.json)";

		private Config()
		{
			foreach (KeyValuePair<string, bool> d in Defaults)
			{
				on[d.Key] = d.Value;
			}
		}

		internal bool On(string key)
		{
			return on.TryGetValue(key, out bool v) && v;
		}

		internal static Config Load()
		{
			var c = new Config();
			string path = null;
			try
			{
				string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
				if (string.IsNullOrEmpty(dir))
				{
					return c;
				}
				path = Path.Combine(dir, "config.json");
				if (!File.Exists(path))
				{
					return c;
				}
				JObject file = JObject.Parse(File.ReadAllText(path));
				string notes = "";
				foreach (KeyValuePair<string, JToken> kv in file)
				{
					if (kv.Key == "brainRateHz" || kv.Key == "steamPollCallbacksHz")
					{
						if (kv.Value.Type != JTokenType.Float && kv.Value.Type != JTokenType.Integer)
						{
							notes += " (" + kv.Key + " is not a number, ignored)";
							continue;
						}
						float hz = Clamp(kv.Value.Value<float>(), kv.Key, ref notes);
						if (kv.Key == "brainRateHz")
						{
							c.BrainRateHz = hz;
						}
						else
						{
							c.SteamPollCallbacksHz = hz;
						}
					}
					else if (kv.Key == "loadGcHoldRamGb")
					{
						if (kv.Value.Type != JTokenType.Float && kv.Value.Type != JTokenType.Integer)
						{
							notes += " (" + kv.Key + " is not a number, ignored)";
							continue;
						}
						double gb = kv.Value.Value<double>();
						if (gb < 0 || gb > 4096)
						{
							notes += " (" + kv.Key + " " + gb + " out of 0-4096, ignored)";
							continue;
						}
						c.LoadGcHoldRamGb = gb;
					}
					else if (!c.on.ContainsKey(kv.Key))
					{
						notes += " (unknown key " + kv.Key + " ignored)";
					}
					else if (kv.Value.Type != JTokenType.Boolean)
					{
						notes += " (" + kv.Key + " is not true/false, ignored)";
					}
					else
					{
						c.on[kv.Key] = kv.Value.Value<bool>();
					}
				}
				c.Source = "config.json" + notes;
			}
			catch (Exception e)
			{
				PerfLog.Warn("could not read " + (path ?? "config.json") + ", using the defaults: "
					+ e.GetType().Name + ": " + e.Message);
				return new Config { Source = "defaults (config.json could not be read)" };
			}
			return c;
		}

		private static float Clamp(float v, string name, ref string notes)
		{
			if (v >= MinHz && v <= MaxHz)
			{
				return v;
			}
			float c = v < MinHz ? MinHz : MaxHz;
			notes += " (" + name + " " + v + " clamped to " + c + ")";
			return c;
		}

		internal string Describe()
		{
			var off = new List<string>();
			foreach (KeyValuePair<string, bool> d in Defaults)
			{
				if (!On(d.Key))
				{
					off.Add(d.Key);
				}
			}
			return (Defaults.Length - off.Count) + " of " + Defaults.Length + " on"
				+ (off.Count > 0 ? "; off: " + string.Join(", ", off.ToArray()) : "")
				+ (On("brainRate") ? "; brainRateHz " + BrainRateHz : "")
				+ (On("steamPoll") ? "; steamPollCallbacksHz " + SteamPollCallbacksHz : "")
				+ (On("loadGcHold") && LoadGcHoldRamGb > 0 ? "; loadGcHoldRamGb " + LoadGcHoldRamGb : "")
				+ "; from " + Source;
		}
	}
}
