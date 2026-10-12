using System;
using UnityEngine;

namespace OniPerf
{
	/// <summary>
	/// The levers' log. Never Debug.LogError: ONI's crash reporter treats an error log like an
	/// unhandled exception, pauses the game and disables the mod.
	///
	/// The lever sources are also compiled into the bench harness, which sets <see cref="Sink"/> so
	/// their lines go to its own log; in this mod it stays null and every line goes to Player.log.
	/// </summary>
	internal static class PerfLog
	{
		private const string Prefix = "[OniPerf] ";

		/// <summary>When set, receives every line instead of Player.log; the bool is true for a warning.</summary>
		public static System.Action<string, bool> Sink;

		public static void Info(string message)
		{
			if (Sink != null)
			{
				Sink(message, false);
				return;
			}
			Debug.Log(Prefix + message);
		}

		public static void Warn(string message)
		{
			if (Sink != null)
			{
				Sink(message, true);
				return;
			}
			Debug.LogWarning(Prefix + message);
		}
	}
}
