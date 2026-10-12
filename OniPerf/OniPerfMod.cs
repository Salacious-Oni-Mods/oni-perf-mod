using System;
using HarmonyLib;
using KMod;

namespace OniPerf
{
	/// <summary>
	/// Entry point. Each lever is a set of Harmony patches that skips work whose result nothing
	/// reads (the reasoning is in each lever's header), and each one is switched by a key in
	/// <c>config.json</c> (<see cref="Config"/>).
	///
	/// base.OnLoad is deliberately not called: it runs PatchAll, and every patch here is applied by
	/// hand, only for a lever that is switched on. A lever that cannot find the game code it was
	/// written against logs a warning and stays off; the others still load.
	/// </summary>
	public sealed class OniPerfMod : UserMod2
	{
		private static Config config;

		public override void OnLoad(Harmony harmony)
		{
			config = Config.Load();
			PerfLog.Info("loaded " + typeof(OniPerfMod).Assembly.GetName().Version + "; levers: " + config.Describe());
			Arm("cpuFix", () => CpuFix.Install(harmony));
			Arm("screenSleep", () =>
			{
				ScreenSleep.Enabled = true;
				ScreenSleep.AutoSleep = true;
				ScreenSleep.SleepToggles = true;
				ScreenSleep.SleepGraphics = true;
				ScreenSleep.SleepHover = true;
				ScreenSleep.SleepScroll = true;
				ScreenSleep.Install(harmony);
			});
			Arm("propTex", () => { PropTex.Active = true; PropTex.Install(harmony); });
			Arm("lerpBlit", () => { LerpBlit.Active = true; LerpBlit.Install(harmony); });
			Arm("idleSleep", () => { IdleSleep.Active = true; IdleSleep.Install(harmony); });
			Arm("idleCams", () => IdleCams.Install(harmony));
			Arm("workerSleep", () => WorkerSleep.Install(harmony));
			Arm("pipeCache", () => { PipeCache.Active = true; PipeCache.Install(harmony); });
			Arm("achieveRate", () => { AchieveRate.Active = true; AchieveRate.Install(harmony); });
			Arm("pausedSkip", () => { PausedSkip.Anim = true; PausedSkip.Names = true; PausedSkip.Install(harmony); });
			Arm("hoverCache", () => { HoverCache.Active = true; HoverCache.Install(harmony); });
			Arm("symbolMemo", () => { SymbolMemo.Active = true; SymbolMemo.Install(harmony); });
			Arm("overrideRange", () => { OverrideRange.Active = true; OverrideRange.Install(harmony); });
			Arm("moveHold", () => { MoveHold.Active = true; MoveHold.Install(harmony); });
			Arm("parkAnims", () => { ParkAnims.Active = true; ParkAnims.Install(harmony); });
			Arm("markSkip", () => { MarkSkip.Active = true; MarkSkip.Install(harmony); });
			Arm("barRate", () => { BarRate.Active = true; BarRate.Install(harmony); });
			Arm("mixRate", () => { MixRate.Active = true; MixRate.Install(harmony); });
			Arm("raycastCache", () => { RaycastCache.Active = true; RaycastCache.Install(harmony); });
			Arm("steamPoll", () =>
			{
				SteamPoll.SkipRunFrame = true;
				SteamPoll.CallbacksHz = config.SteamPollCallbacksHz;
				SteamPoll.Install(harmony);
			});
			Arm("brainRate", () =>
			{
				BrainRate.Mode = BrainRate.Shape.Spread;
				BrainRate.RunningHz = config.BrainRateHz;
				BrainRate.PausedHz = config.BrainRateHz;
				BrainRate.Active = true;
				BrainRate.Install(harmony);
			});
			Arm("conveyorPaths", () =>
			{
				ConveyorPaths.Check = config.On("conveyorPathsCheck");
				ConveyorPaths.Install(harmony);
				LoadHold.Reports += ConveyorPaths.Report;
			});
			Arm("typeNameCache", () => TypeNameCache.Install(harmony));
			Arm("smRestore", () =>
			{
				SmRestore.Install(harmony);
				LoadHold.Reports += SmRestore.Report;
			});
			// Always on for the load timing line; the collector is held only with loadGcHold.
			try
			{
				LoadHold.HoldWanted = config.On("loadGcHold");
				LoadHold.AssumeRamGb = config.LoadGcHoldRamGb;
				LoadHold.Install(harmony);
			}
			catch (Exception e)
			{
				PerfLog.Warn("loadhold: could not be switched on, left off: " + e.GetType().Name + ": " + e.Message);
			}
			// After LoadHold: the fast loader holds the collector under LoadHold's cap, and not at all
			// when LoadHold left the hold off. A fast load replaces SaveLoader.OnSpawn's call to
			// SaveLoader.Load, so LoadHold's own hold never runs during one.
			Arm("fastLoad", () =>
			{
				AsyncLoad.Install(harmony);
				DrainPause.Install(harmony);
				DrainQuiet.Install(harmony);
				DrainCoalesce.Install(harmony);
			});
			if (config.On("idleCams") || config.On("workerSleep"))
			{
				// Both act on objects that exist only in a loaded colony, so they switch on with it.
				harmony.Patch(AccessTools.DeclaredMethod(typeof(Game), "OnSpawn"),
					postfix: new HarmonyMethod(typeof(OniPerfMod), nameof(GameOnSpawnPostfix)));
			}
		}

		private static void Arm(string key, System.Action install)
		{
			if (!config.On(key))
			{
				return;
			}
			try
			{
				install();
			}
			catch (Exception e)
			{
				PerfLog.Warn(key + ": could not be switched on, left off: " + e.GetType().Name + ": " + e.Message);
			}
		}

		private static void GameOnSpawnPostfix()
		{
			if (config.On("idleCams"))
			{
				IdleCams.Active = true;
				IdleCams.Cursor = true;
				IdleCams.LogSummary("on for this colony");
			}
			if (config.On("workerSleep"))
			{
				// The list from a previous colony holds only destroyed components; clear it.
				WorkerSleep.WakeAll();
				WorkerSleep.Active = true;
				WorkerSleep.LogSummary("on for this colony");
			}
		}
	}
}
