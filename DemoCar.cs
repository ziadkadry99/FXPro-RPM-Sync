using System;

namespace User.FXProRpmSync
{
    /// <summary>
    /// Lap simulation that animates every dash value (ported from FXProDashes/DemoCar.cs): a GT-ish car on a loop of
    /// straights and corners with shifts, braking, lap timing, delta, fuel, temperatures, gaps, flags, pit stops and
    /// the in-car adjustments. Writes SimPro's struct in its base units (km/h, °C, psi, litres; gaps in 1/100 s).
    /// </summary>
    internal sealed class DemoCar
    {
        private struct Segment
        {
            public double Length;   // m
            public double Speed;    // corner speed km/h, or 0 for a straight
            public bool Drs;
            public Segment(double length, double speed, bool drs = false) { Length = length; Speed = speed; Drs = drs; }
        }

        private static readonly Segment[] Track =
        {
            new Segment(950, 0, drs: true), new Segment(110, 95), new Segment(380, 0), new Segment(160, 145),
            new Segment(520, 0), new Segment(90, 75), new Segment(260, 0), new Segment(220, 175),
            new Segment(610, 0), new Segment(130, 115), new Segment(180, 0), new Segment(140, 130),
        };

        private const double MaxRpm = 8200, ShiftRpm = 7900, IdleRpm = 1100;
        private const double BrakeDecel = 14.0;                 // m/s^2
        private const double DownshiftRpm = 4800;
        private static readonly double[] KmhAtMaxRpm = { 0, 72, 106, 140, 178, 222, 300 }; // per gear
        private const int TopGear = 6;
        private const double TankLitres = 90;
        private const int PitEveryLaps = 6;

        private readonly Random rng = new Random();
        private readonly double trackLength;
        private readonly double[] bestLapTimeAt, currentLapTimeAt;   // elapsed time per 10 m, for the delta
        private const double Bucket = 10;

        private double distance;        // m into the lap
        private double speed;           // m/s
        private int gear = 1;
        private double rpm = IdleRpm;
        private double shiftTimer;      // s of throttle cut left during an upshift
        private double lapTime;         // s
        private double bestLap = double.MaxValue, lastLap;
        private int lap = 1, position = 6;
        private double pace = 1.0;      // this lap's corner-speed factor
        private double fuel = 62, fuelAtLapStart = 62, fuelPerLap;
        private readonly double[] tyreTemp = { 72, 74, 70, 71 };
        private readonly double[] brakeTemp = { 180, 185, 150, 152 };
        private readonly double[] tyreWear = { 100, 100, 100, 100 };
        private readonly double[] wearRate = { 1.05, 1.15, 0.85, 0.9 };  // FR works hardest on this track
        private double water = 84, oil = 92;
        private double gapAhead = 1.8, gapBehind = 1.2;
        private double throttle, brake, clutch = 100;
        private double absPulse;
        private double ers = 100;       // % state of charge
        private double pitTimer;        // s left in the pit lane
        private double flagTimer, flagKind; // brief blue/yellow flags now and then
        private double launch = 2.0;    // s of clutch slip at the start

        public DemoCar()
        {
            foreach (var s in Track) trackLength += s.Length;
            bestLapTimeAt = new double[(int)(trackLength / Bucket) + 2];
            currentLapTimeAt = new double[bestLapTimeAt.Length];
            NewLapPace();
        }

        public void Step(double dt, SimProTelemetry t)
        {
            if (dt <= 0) return;
            if (dt > 0.1) dt = 0.1;

            Drive(dt);
            Physics(dt);
            Timing(dt);
            Temperatures(dt);
            Publish(t);
        }

        private void Drive(double dt)
        {
            int si = SegmentAt(distance, out double into);
            var seg = Track[si];
            var next = Track[(si + 1) % Track.Length];
            double remaining = seg.Length - into;
            double kmh = speed * 3.6;

            double wantThrottle, wantBrake = 0;
            double nextCorner = next.Speed > 0 ? next.Speed * pace : 0;
            double brakingDist = nextCorner > 0 ? (speed * speed - Math.Pow(nextCorner / 3.6, 2)) / (2 * BrakeDecel) : 0;

            if (pitTimer > 0)
            {
                // Pit lane: hold 60 km/h on the limiter.
                wantThrottle = kmh < 60 ? 0.6 : 0;
                wantBrake = kmh > 64 ? 0.5 : 0;
            }
            else if (nextCorner > 0 && kmh > nextCorner + 2 && remaining < brakingDist + 8)
            {
                wantThrottle = 0;
                wantBrake = Math.Min(1, 0.55 + (brakingDist - remaining + 20) / 60);
            }
            else if (seg.Speed > 0)
            {
                double target = seg.Speed * pace;
                double exit = into / seg.Length;
                wantThrottle = Math.Max(0, Math.Min(1, 0.35 + (target - kmh) * 0.08 + exit * 0.5));
                if (kmh > target + 6) { wantThrottle = 0; wantBrake = 0.25; }
            }
            else
            {
                wantThrottle = 1;
            }

            throttle = Approach(throttle, wantThrottle, dt * 6);
            brake = Approach(brake, wantBrake, dt * 8);

            // Clutch: slipped on launch, dipped briefly on each upshift (% pressed).
            if (launch > 0) { launch -= dt; clutch = Math.Max(0, launch / 2.0 * 100); }
            else clutch = shiftTimer > 0 ? 60 : Approach(clutch, 0, dt * 10);
        }

        private void Physics(double dt)
        {
            double effThrottle = shiftTimer > 0 ? 0 : throttle;
            if (shiftTimer > 0) shiftTimer -= dt;

            double v = Math.Max(speed, 3);
            double drive = effThrottle * Math.Min(9.5, 330 / v);          // power-limited
            double drag = 0.00028 * speed * speed * speed / Math.Max(v, 1) + 0.15;
            double decel = brake * BrakeDecel;
            speed = Math.Max(0, speed + (drive - drag - decel) * dt);
            distance += speed * dt;

            double kmh = speed * 3.6;
            rpm = Math.Max(IdleRpm, kmh / KmhAtMaxRpm[gear] * MaxRpm);
            if (rpm >= ShiftRpm && gear < TopGear && throttle > 0.5)
            {
                gear++;
                shiftTimer = 0.07;
            }
            else if (gear > 1 && rpm < DownshiftRpm && (brake > 0.1 || throttle < 0.4))
            {
                double lower = kmh / KmhAtMaxRpm[gear - 1] * MaxRpm;
                if (lower < ShiftRpm - 300) gear--;
            }
            rpm = Math.Min(MaxRpm, Math.Max(IdleRpm, kmh / KmhAtMaxRpm[gear] * MaxRpm));
            absPulse = brake > 0.8 && kmh > 40 ? absPulse + dt : 0;

            // ERS: harvest under braking, deploy on straights.
            ers = Math.Max(0, Math.Min(100, ers + (brake * 9 - throttle * (kmh > 150 ? 4.5 : 0)) * dt));
        }

        private void Timing(double dt)
        {
            lapTime += dt;
            if (pitTimer > 0) pitTimer -= dt;
            int bucket = (int)(Math.Min(distance, trackLength) / Bucket);
            if (bucket < currentLapTimeAt.Length) currentLapTimeAt[bucket] = lapTime;

            if (distance >= trackLength)
            {
                distance -= trackLength;
                lastLap = lapTime;
                if (lapTime < bestLap)
                {
                    bestLap = lapTime;
                    Array.Copy(currentLapTimeAt, bestLapTimeAt, currentLapTimeAt.Length);
                }
                lapTime = 0;
                lap++;
                fuelPerLap = fuelAtLapStart - fuel;
                for (int i = 0; i < 4; i++) tyreWear[i] = Math.Max(0, tyreWear[i] - wearRate[i] * (0.8 + rng.NextDouble() * 0.4));
                if (rng.NextDouble() < 0.35) position = Math.Max(1, Math.Min(20, position + (rng.NextDouble() < 0.55 ? -1 : 1)));
                if ((lap - 1) % PitEveryLaps == 0)
                {
                    // Pit stop: 20 s in the pit lane, fresh tyres, fuel topped up.
                    pitTimer = 20;
                    fuel = TankLitres * 0.7;
                    for (int i = 0; i < 4; i++) tyreWear[i] = 100;
                }
                fuelAtLapStart = fuel;
                NewLapPace();
            }

            gapAhead = Math.Max(0.1, gapAhead + (rng.NextDouble() - 0.5) * dt * 0.4);
            gapBehind = Math.Max(0.1, gapBehind + (rng.NextDouble() - 0.5) * dt * 0.4);
            fuel = Math.Max(0, fuel - (0.004 + throttle * 0.045) * dt);

            // A blue or yellow flag for a few seconds, about once a minute.
            if (flagTimer > 0) flagTimer -= dt;
            else if (rng.NextDouble() < dt / 60) { flagTimer = 4; flagKind = rng.NextDouble() < 0.5 ? 2 : 3; }
        }

        private void Temperatures(double dt)
        {
            int si = SegmentAt(distance, out _);
            bool corner = Track[si].Speed > 0;
            for (int i = 0; i < 4; i++)
            {
                bool front = i < 2;
                double target = 78 + (corner ? 18 : 0) + (front ? brake * 10 : throttle * 6) + (i == 1 ? 4 : 0);
                tyreTemp[i] = Approach(tyreTemp[i], target, dt * 1.5);
                double heat = brake * (front ? 520 : 330) * dt;
                double cool = (brakeTemp[i] - 120) * (0.05 + speed * 0.004) * dt;
                brakeTemp[i] = Math.Min(1023, Math.Max(80, brakeTemp[i] + heat - cool));
            }
            water = Approach(water, 84 + throttle * 8, dt * 0.05);
            oil = Approach(oil, 92 + throttle * 12, dt * 0.04);
        }

        private void Publish(SimProTelemetry t)
        {
            t.Clear();
            double kmh = speed * 3.6;
            int si = SegmentAt(distance, out double into);

            t.Set("isGameRunning", true);
            t.Set("isEngineRunning", true);
            t.Set("isEngineIgnitionOn", true);
            t.Set("engineStarted", 1);
            t.Set("speed", kmh);
            t.Set("rpm", rpm);
            t.Set("maxRpm", MaxRpm);
            t.Set("rpmPercentage", rpm / MaxRpm * 100);
            t.Set("gear", gear);
            t.Set("maxGears", TopGear);
            t.Set("throttle", throttle * 100);
            t.Set("brake", brake * 100);
            t.Set("clutch", clutch);

            t.Set("isAbsActive", absPulse > 0 && (int)(absPulse * 12) % 2 == 0);
            t.Set("isTcActive", throttle > 0.9 && gear <= 2 && rng.NextDouble() < 0.3);
            t.Set("isPitLimiterOn", pitTimer > 0);
            t.Set("isInPitLane", pitTimer > 0);
            t.Set("isDrsAvaiable", Track[si].Drs && gapAhead < 1.0);
            t.Set("isDrsEnabled", Track[si].Drs && into > 150 && gapAhead < 1.0);
            t.Set("absLevel", 4);
            t.Set("tcLevel", 3);
            t.Set("tcCut", 2);
            t.Set("engineMap", 1 + (lap / 3) % 4);

            t.Set("completedLaps", lap - 1);
            t.Set("currentLap", lap);
            t.Set("position", position);
            t.Set("currentLapTime", lapTime * 1000);
            t.Set("lastLapTime", lastLap * 1000);
            t.Set("bestLapTime", bestLap == double.MaxValue ? 0 : bestLap * 1000);
            t.Set("gainLoss", Delta());
            t.Set("greenFlag", flagTimer <= 0);
            t.Set("blueFlag", flagTimer > 0 && flagKind == 2);
            t.Set("yellowFlag", flagTimer > 0 && flagKind == 3);
            // Gaps: hundredths of a second (the wheel shows value / 100).
            t.Set("gapAhead", Math.Round(gapAhead * 100));
            t.Set("gapBehind", Math.Round(gapBehind * 100));

            t.Set("waterTemperature", water);
            t.Set("oilTemperature", oil);
            t.Set("oilPressure", 45 + rpm / MaxRpm * 30);   // psi
            t.Set("turbo", throttle * rpm / MaxRpm * 180);

            for (int i = 0; i < 4; i++)
            {
                t.Set("tyreTemperature" + i, tyreTemp[i]);
                t.Set("tyreTemperatureInner" + i, tyreTemp[i] + 4);
                t.Set("tyreTemperatureMiddle" + i, tyreTemp[i]);
                t.Set("tyreTemperatureOuter" + i, tyreTemp[i] - 3);
                t.Set("tyrePressure" + i, 26.2 + (tyreTemp[i] - 80) * 0.035); // psi
                t.Set("brakeTemperature" + i, brakeTemp[i]);
                t.Set("tyreWear" + i, tyreWear[i]);
            }

            t.Set("fuel", Math.Min(102.3, fuel));
            t.Set("maxFuel", TankLitres);
            t.Set("fuelPerLap", Math.Round(fuelPerLap * 10)); // L/lap x10
            t.Set("fuelLitersPerLap", fuelPerLap);
            t.Set("brakeBias", 56.2);
            t.Set("ersPercent", ers);
            t.Set("ersMode", 1 + (lap / 2) % 4);
            t.Set("frontAntiRollBar", 4);
            t.Set("rearAntiRollBar", 3);
            t.Set("diffAdjOnThrottle", 5);
            t.Set("throttleShape", 2);
            t.Set("engineBraking", 3);
            t.Set("diffEntry", 6);
            t.Set("diffMiddle", 4);
            t.Set("diffExit", 7);

            t.SetString("carModelString", "FXPro RPM Sync demo");
            t.SetString("trackNameString", "Demo loop");
            t.SetString("sessionTypeName", "Race");
        }

        /// <summary>Seconds gained (-) or lost (+) against the best lap at the same distance.</summary>
        private double Delta()
        {
            if (bestLap == double.MaxValue) return 0;
            int bucket = (int)(Math.Min(distance, trackLength) / Bucket);
            if (bucket >= bestLapTimeAt.Length || bestLapTimeAt[bucket] <= 0) return 0;
            return lapTime - bestLapTimeAt[bucket];
        }

        private void NewLapPace() => pace = 0.96 + rng.NextDouble() * 0.05;

        private static int SegmentAt(double d, out double into)
        {
            for (int i = 0; i < Track.Length; i++)
            {
                if (d < Track[i].Length) { into = d; return i; }
                d -= Track[i].Length;
            }
            into = 0;
            return 0;
        }

        private static double Approach(double cur, double target, double step) =>
            cur < target ? Math.Min(target, cur + step * Math.Max(1, Math.Abs(target - cur)))
                         : Math.Max(target, cur - step * Math.Max(1, Math.Abs(target - cur)));
    }
}
