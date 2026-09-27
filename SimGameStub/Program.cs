using System;
using System.Diagnostics;
using System.Threading;

namespace SimGameStub
{
    /// <summary>
    /// Does nothing but exist: SimPro Manager treats a running process named simgame.exe as a game and reads the
    /// shared memory "/simgame" that the FXPro RPM Sync plugin fills.
    ///
    /// It outlives a SimHub restart on purpose: if it disappeared, SimPro would switch to the real game (if one is
    /// running) and, in SimPro 3.2.2, not come back to SimGame until SimPro restarts. So it keeps running while SimHub
    /// runs or restarts (the restarted plugin takes it over), and exits once SimHub has been gone for 30 s. The plugin
    /// kills it directly when the option is turned off.
    /// </summary>
    internal static class Program
    {
        private static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);

        private static int Main(string[] args)
        {
            if (args.Length > 0 && int.TryParse(args[0], out var parentId))
            {
                try { Process.GetProcessById(parentId).WaitForExit(); }
                catch (ArgumentException) { } // parent already gone
            }

            var goneSince = DateTime.UtcNow;
            while (DateTime.UtcNow - goneSince < Grace)
            {
                Thread.Sleep(1000);
                if (Process.GetProcessesByName("SimHubWPF").Length > 0) goneSince = DateTime.UtcNow;
            }
            return 0;
        }
    }
}
