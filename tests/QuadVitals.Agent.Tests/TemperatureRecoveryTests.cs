using System;
using System.Reflection;
using System.Threading;
using LibreHardwareMonitor.Hardware;
using QuadVitals.Core;

namespace QuadVitals.Agent
{
    internal static class TemperatureRecoveryTests
    {
        private static int _passed;
        private static readonly DateTime Start = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

        private static int Main(string[] args)
        {
            try
            {
                if (args.Length > 0 && args[0] == "--missing-library") MissingLibrary();
                else
                {
                    StartupTimeout();
                    RecoveryAndRuntimeTimeout();
                    InitialOpenFailure();
                    MissingDriver();
                    PowerModule();
                    RestartBudget();
                }
                Console.WriteLine("temperature recovery: " + _passed + " passed / 0 failed");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAIL " + ex);
                return 1;
            }
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
            _passed++;
            Console.WriteLine("PASS " + message);
        }

        private static void Observe(TemperatureWorker worker, double value, int seconds)
        {
            typeof(TemperatureWorker).GetMethod("Supervise", PrivateInstance).Invoke(worker,
                new object[] { value, Start.AddSeconds(seconds) });
        }

        private static TemperatureWorker StoppedWorker(TemperatureSource source, Action<string> callback)
        {
            TemperatureWorker worker = new TemperatureWorker(null, 500, callback);
            Thread thread = (Thread)typeof(TemperatureWorker).GetField("_thread", PrivateInstance).GetValue(worker);
            Check(thread.Join(2000), "unconfigured worker exits");
            typeof(TemperatureWorker).GetField("_source", PrivateInstance).SetValue(worker, source);
            return worker;
        }

        private static TemperatureSource Source()
        {
            string error;
            return TemperatureSource.Create(out error);
        }

        private static void StartupTimeout()
        {
            int requests = 0;
            Computer.Empty = true;
            using (TemperatureWorker worker = StoppedWorker(Source(), reason => requests++))
            {
                int opens = Computer.Opens;
                Observe(worker, double.NaN, 0);
                Observe(worker, double.NaN, 19);
                Check(Computer.Opens == opens && requests == 0, "startup grace does not reopen immediately");
                Observe(worker, double.NaN, 20);
                Check(Computer.Opens == opens + 1, "silent startup reopens the session after 20 seconds");
                Observe(worker, double.NaN, 59);
                Check(requests == 0, "startup does not restart before 60 seconds");
                Observe(worker, double.NaN, 60);
                Check(requests == 1, "startup restarts at 60 seconds despite reopen cooldown");
                Observe(worker, double.NaN, 80);
                Observe(worker, double.NaN, 660);
                Check(requests == 1, "one outage requests only one process replacement");
            }
        }

        private static void RecoveryAndRuntimeTimeout()
        {
            int requests = 0;
            using (TemperatureWorker worker = StoppedWorker(Source(), reason => requests++))
            {
                Observe(worker, double.NaN, 0);
                Observe(worker, double.NaN, 20);
                Observe(worker, 55, 25);
                Observe(worker, 55, 60);
                Check(requests == 0, "recovered startup cancels process replacement");
                Observe(worker, double.NaN, 100);
                Observe(worker, double.NaN, 160);
                Observe(worker, double.NaN, 699);
                Check(requests == 0, "previously healthy sampling keeps the 10 minute grace");
                Observe(worker, double.NaN, 700);
                Check(requests == 1, "runtime stall escalates after 10 minutes independent of reopen cooldown");
                Observe(worker, 55, 701);
                Observe(worker, double.NaN, 702);
                Observe(worker, double.NaN, 1302);
                Check(requests == 2, "a recovered session can report a later outage");
            }
        }

        private static void InitialOpenFailure()
        {
            Computer.Empty = false;
            Computer.OpenFailures = 1;
            int closed = Computer.Closes;
            TemperatureSource source = Source();
            Check(!source.IsAvailable && source.CanRetry, "failed driver Open remains retryable");
            Check(Computer.Closes == closed + 1, "failed Open closes the partial session");
            int requests = 0;
            using (TemperatureWorker worker = new TemperatureWorker(source, 500, reason => requests++))
            {
                FieldInfo failure = typeof(TemperatureWorker).GetField("_failedSinceUtc", PrivateInstance);
                Check(SpinWait.SpinUntil(() => (DateTime)failure.GetValue(worker) != DateTime.MinValue, 2000),
                    "failed initial Open keeps the sampling thread alive");
                failure.SetValue(worker, DateTime.UtcNow.AddSeconds(-21));
                double celsius = double.NaN;
                string reason = null;
                Check(SpinWait.SpinUntil(() => worker.TryGet(DateTime.UtcNow, TimeSpan.FromSeconds(12),
                    out celsius, out reason), 4000), "initial Open failure recovers through the real worker loop");
                double watts;
                MetricsSnapshot.FanReading[] fans;
                Check(celsius == 55, "temperature recovers");
                Check(worker.TryGetPower(DateTime.UtcNow, TimeSpan.FromSeconds(12), out watts) && watts == 30,
                    "CPU power recovers with temperature");
                Check(worker.TryGetFans(DateTime.UtcNow, TimeSpan.FromSeconds(12), out fans) &&
                    fans.Length == 1 && fans[0].Rpm == 900, "fan readings recover with temperature");
                Check(requests == 0, "successful session retry avoids a process restart");
            }
        }

        private static void RestartBudget()
        {
            Check(new AgentRestarter().Request("test", null, false), "first automatic restart is allowed");
            Check(!new AgentRestarter().Request("test", null, false),
                "persistent hourly guard blocks a replacement process from looping");
        }

        private static void MissingDriver()
        {
            LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled = false;
            try
            {
                using (TemperatureSource source = Source())
                {
                    Check(source.PawnIoMissing && !source.IsAvailable && !source.CanRetry,
                        "missing PawnIO is diagnosed without attempting the legacy driver");
                    int requests = 0;
                    using (TemperatureWorker worker = new TemperatureWorker(source, 500, reason => requests++))
                    {
                        Thread thread = (Thread)typeof(TemperatureWorker).GetField("_thread", PrivateInstance).GetValue(worker);
                        Check(thread.Join(2000) && requests == 0 && worker.PawnIoMissing,
                            "missing PawnIO keeps the restart loop stopped");
                    }
                }
            }
            finally { LibreHardwareMonitor.PawnIo.PawnIo.IsInstalled = true; }
            LibreHardwareMonitor.PawnIo.PawnIo.Version = new Version(2, 1, 0, 0);
            try
            {
                using (TemperatureSource source = Source())
                    Check(source.PawnIoMissing && !source.CanRetry, "an outdated PawnIO driver requires an update without restarting repeatedly");
            }
            finally { LibreHardwareMonitor.PawnIo.PawnIo.Version = new Version(2, 2, 0, 0); }
        }

        private static void PowerModule()
        {
            var session = new LibreHardwareMonitor.PawnIo.IntelMsr();
            MethodInfo read = session.GetType().GetMethod("ReadMsr");
            MethodInfo watts = typeof(CpuPowerLimitSource).GetMethod("ReadWatts", BindingFlags.NonPublic | BindingFlags.Static);
            Check((double)watts.Invoke(null, new object[] { read, session, 0x614u, 0 }) == 125,
                "PawnIO instance MSR read preserves base TDP decoding");
            Check((double)watts.Invoke(null, new object[] { read, session, 0x610u, 0 }) == 253,
                "PawnIO instance MSR read preserves power limit decoding");
            LibreHardwareMonitor.PawnIo.IntelMsr.FailRead = true;
            try
            {
                Check((double)watts.Invoke(null, new object[] { read, session, 0x610u, 0 }) == 0,
                    "failed PawnIO read cannot publish an output value");
            }
            finally { LibreHardwareMonitor.PawnIo.IntelMsr.FailRead = false; }
        }

        private static void MissingLibrary()
        {
            using (TemperatureSource source = Source())
            {
                Check(!source.IsAvailable && !source.CanRetry, "missing optional library stays unavailable");
                int requests = 0;
                using (TemperatureWorker worker = new TemperatureWorker(source, 500, reason => requests++))
                {
                    Thread thread = (Thread)typeof(TemperatureWorker).GetField("_thread", PrivateInstance).GetValue(worker);
                    Check(thread.Join(2000) && requests == 0, "missing library never starts a recovery loop");
                }
            }
        }
    }
}
