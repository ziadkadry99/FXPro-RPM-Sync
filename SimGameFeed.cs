using System;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;

namespace User.FXProRpmSync
{
    /// <summary>
    /// Feeds SimPro Manager through its built-in "SimGame" source: while a process named simgame.exe runs, SimPro
    /// treats it as the running game and copies _STelemetryData from the shared memory "/simgame" on every poll
    /// (SimgameReader::pollData is a plain memcpy). SimPro then converts and sends it to the wheel like any game's data.
    ///
    /// SimPro picks a game only while none is selected (STelemetryManager::serviceThread, every 2 s) and keeps it until
    /// that game's process exits, so SimGame has to be running before the real game starts.
    /// </summary>
    internal sealed class SimGameFeed : IDisposable
    {
        private const string MappingName = "/simgame";
        private const int MappingSize = 0x1000;
        private const string StubResource = "User.FXProRpmSync.simgame.exe";

        private readonly string stubPath;
        private MemoryMappedFile mapping;
        private MemoryMappedViewAccessor view;
        private Process stub;

        public SimGameFeed(string dataFolder)
        {
            stubPath = Path.Combine(dataFolder, "simgame.exe");
        }

        public bool Running => stub != null && !SafeHasExited(stub);

        /// <summary>
        /// Creates the mapping, then starts simgame.exe. The mapping must exist first: SimPro runs elevated, and if it
        /// created the mapping itself a normal process might not be allowed to open it.
        /// </summary>
        public void Start(byte[] initial)
        {
            if (mapping == null)
            {
                mapping = MemoryMappedFile.CreateOrOpen(MappingName, MappingSize);
                view = mapping.CreateViewAccessor(0, MappingSize);
            }
            Write(initial);
            if (Running) return;

            // Take over the stub from before a SimHub restart: it kept SimPro on SimGame in the meantime.
            stub = FindOwnStub();
            if (stub != null) return;

            ExtractStub();
            stub = Process.Start(new ProcessStartInfo(stubPath, Process.GetCurrentProcess().Id.ToString())
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }

        /// <summary>Tells SimPro there's no game but keeps simgame.exe (and so SimPro's choice of SimGame) alive.</summary>
        public void Detach()
        {
            if (view != null) Write(new byte[SimProTelemetry.Size]);
            stub = null;
        }

        private Process FindOwnStub()
        {
            foreach (var p in Process.GetProcessesByName("simgame"))
            {
                try
                {
                    if (string.Equals(p.MainModule?.FileName, stubPath, StringComparison.OrdinalIgnoreCase) && !p.HasExited) return p;
                }
                catch { }
            }
            return null;
        }

        public void Write(byte[] data)
        {
            view?.WriteArray(0, data, 0, data.Length);
        }

        /// <summary>Stops simgame.exe (SimPro drops SimGame and can pick up a real game again) and zeroes the data.</summary>
        public void Stop()
        {
            try { if (Running) stub.Kill(); }
            catch { }
            stub = FindOwnStub(); // also one left from a previous SimHub run
            try { stub?.Kill(); }
            catch { }
            stub = null;
            if (view != null) Write(new byte[SimProTelemetry.Size]);
        }

        /// <summary>SimHub exiting: leave simgame.exe running (see Detach) and release the mapping.</summary>
        public void Dispose()
        {
            Detach();
            view?.Dispose();
            mapping?.Dispose();
            view = null;
            mapping = null;
        }

        private void ExtractStub()
        {
            using (var res = typeof(SimGameFeed).Assembly.GetManifestResourceStream(StubResource))
            {
                if (res == null) throw new InvalidOperationException("simgame.exe is missing from the plugin");
                var bytes = new byte[res.Length];
                int read = 0;
                while (read < bytes.Length) read += res.Read(bytes, read, bytes.Length - read);
                if (File.Exists(stubPath) && File.ReadAllBytes(stubPath).SequenceEqual(bytes)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(stubPath));
                File.WriteAllBytes(stubPath, bytes);
            }
        }

        private static bool SafeHasExited(Process p)
        {
            try { return p.HasExited; }
            catch { return true; }
        }
    }
}
