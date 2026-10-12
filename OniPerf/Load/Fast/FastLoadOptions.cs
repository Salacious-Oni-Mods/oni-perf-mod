namespace OniPerf
{
	/// <summary>
	/// The fast loader's settings. In the test rig these were command-line switches; here they are
	/// fixed at the set measured on the large test colonies:
	///
	///   object loop        400 ms of work a frame behind the loading screen;
	///   deferral           objects more than one screen from the saved camera (and, on Spaced
	///                      Out, every object on another world) are built after the loading screen;
	///   drain              up to 64 ms a frame, steered to 60 fps while the player is interacting
	///                      and 30 fps after a second without input; the game stays paused until it
	///                      ends, and brains, the zero-length paused sim step and property textures
	///                      are thinned meanwhile;
	///   collector          held for the load and the drain under `loadGcHold`'s rule: half the
	///                      installed memory as the cap, at most 24 GB, and not held at all below
	///                      24 GB installed (the load still runs, with collections on).
	///
	/// The two collector switches are read when a load starts, so they follow LoadHold.
	/// </summary>
	internal static class FastLoadOptions
	{
		public static float AsyncLoadBudgetMs = 400f;
		public static float AsyncLoadIntegrationMs = 20f;
		public static bool AsyncLoadNoBatch = false;
		public static bool AsyncLoadGcAfterLoop = false;
		public static bool AsyncLoadNoGc => LoadHold.CapBytes > 0;
		public static bool AsyncLoadDeferObjectNoGc => LoadHold.CapBytes > 0;

		/// <summary>The earlier deferral of Start() calls alone (DeferredSpawn); off, superseded by
		/// the whole-object deferral below.</summary>
		public static float AsyncLoadDeferScreens = -1f;
		public static float AsyncLoadDeferObjectScreens = 1f;
		public static bool AsyncLoadDeferObjectAll = false;
		public static float AsyncLoadDeferObjectNetSec = 1f;
		public static float AsyncLoadDeferMs = 64f;
		public static float AsyncLoadDeferFps = 60f;
		public static float AsyncLoadDeferIdleSec = 1f;
		public static float AsyncLoadDeferIdleFps = 30f;
	}
}
