// Plain-C# tests for HrvMath / PhysiologyWindow / PpiSample. Exit code 0 = all passed.
using System;
using System.Collections.Generic;
using AdaptiveAR.Physiology;

namespace AdaptiveAR.Physiology.Tests
{
    public static class Program
    {
        private static int _failed, _passed;

        private static void Check(string name, bool ok, string detail = "")
        {
            if (ok) { _passed++; Console.WriteLine($"PASS  {name}"); }
            else { _failed++; Console.WriteLine($"FAIL  {name}  {detail}"); }
        }

        private static PpiSample S(int ppi, bool blocker = false, int hr = 70, bool skin = true, bool skinSupported = true, int err = 5)
        {
            return new PpiSample { PpiMs = ppi, Blocker = blocker, DeviceHrBpm = hr, SkinContact = skin, SkinContactSupported = skinSupported, ErrorEstimateMs = err, Source = "test" };
        }

        public static int Main()
        {
            // 1. constant intervals -> RMSSD 0
            double? r1 = HrvMath.Rmssd(new double[] { 800, 800, 800, 800, 800 });
            Check("1 constant intervals -> RMSSD 0", r1.HasValue && Math.Abs(r1.Value) < 1e-9, $"got {r1}");

            // 2. known short sequence: 800, 820, 790, 810 -> diffs 20, -30, 20 -> squares 400, 900, 400 -> mean 566.667 -> sqrt 23.8048
            double? r2 = HrvMath.Rmssd(new double[] { 800, 820, 790, 810 });
            double expected = Math.Sqrt((400.0 + 900.0 + 400.0) / 3.0);
            Check("2 known sequence RMSSD", r2.HasValue && Math.Abs(r2.Value - expected) < 1e-9, $"got {r2} expected {expected}");
            Check("2b mean IBI", Math.Abs(HrvMath.MeanIbi(new double[] { 800, 820, 790, 810 }).Value - 805.0) < 1e-9);
            Check("2c HR from IBI", Math.Abs(HrvMath.HeartRateFromIbi(800).Value - 75.0) < 1e-9);
            Check("2d RMSSD needs 2 intervals", !HrvMath.Rmssd(new double[] { 800 }).HasValue && !HrvMath.Rmssd(new double[0]).HasValue);

            // 3. insufficient samples -> unavailable / null snapshot
            var cfg = new PhysiologyWindowConfig { WindowMs = 60000, MinAccepted = 10, MinPpiMs = 300, MaxPpiMs = 2000 };
            var w = new PhysiologyWindow(cfg);
            double t = 0;
            for (int i = 0; i < 9; i++) { t += 800; w.Admit(S(800 + (i % 2) * 20), t); }
            PhysioSummary s3 = w.Summarize(t);
            Check("3 insufficient samples -> RMSSD null, unavailable", !s3.Available && !s3.RmssdMs.HasValue && s3.NAccepted == 9, $"available={s3.Available} n={s3.NAccepted}");
            Check("3b snapshot null when unavailable", w.Snapshot(t, 15000) == null);

            // 4. blocker sample -> recorded, excluded from the live window
            w = new PhysiologyWindow(cfg); t = 0;
            for (int i = 0; i < 12; i++) { t += 800; w.Admit(S(800), t); }
            t += 800; var blocked = S(400, blocker: true); w.Admit(blocked, t);
            PhysioSummary s4 = w.Summarize(t);
            Check("4 blocker sample excluded but counted", !blocked.Accepted && blocked.RejectReason == PhysiologyWindow.RejectBlocker
                  && s4.NRaw == 13 && s4.NAccepted == 12 && s4.NRejectedBlocker == 1 && Math.Abs(s4.RmssdMs.Value) < 1e-9,
                  $"accepted={blocked.Accepted} reason={blocked.RejectReason} raw={s4.NRaw} acc={s4.NAccepted} rmssd={s4.RmssdMs}");

            // 5. out-of-range sample -> recorded, excluded
            t += 800; var outOfRange = S(2500); w.Admit(outOfRange, t);
            t += 800; var tooShort = S(250); w.Admit(tooShort, t);
            PhysioSummary s5 = w.Summarize(t);
            Check("5 out-of-range samples excluded", !outOfRange.Accepted && outOfRange.RejectReason == PhysiologyWindow.RejectRange
                  && !tooShort.Accepted && s5.NRejectedRange == 2 && s5.NAccepted == 12 && Math.Abs(s5.Quality.Value - 12.0 / 15.0) < 1e-9,
                  $"range={s5.NRejectedRange} acc={s5.NAccepted} q={s5.Quality}");

            // 5b. skin-contact rejection OFF by default, ON when configured
            var noSkin = S(800, skin: false, skinSupported: true); t += 800; w.Admit(noSkin, t);
            Check("5b skin-contact not rejected by default", noSkin.Accepted);
            var wSkin = new PhysiologyWindow(new PhysiologyWindowConfig { RejectNoSkinContact = true });
            var noSkin2 = S(800, skin: false, skinSupported: true); wSkin.Admit(noSkin2, 1000);
            var noSkinUnsupported = S(800, skin: false, skinSupported: false); wSkin.Admit(noSkinUnsupported, 2000);
            Check("5c skin-contact rejected only when configured AND supported", !noSkin2.Accepted && noSkin2.RejectReason == PhysiologyWindow.RejectSkin && noSkinUnsupported.Accepted);

            // 6. valid samples populate the rolling window; old ones leave it
            w = new PhysiologyWindow(new PhysiologyWindowConfig { WindowMs = 10000, MinAccepted = 3 }); t = 0;
            for (int i = 0; i < 20; i++) { t += 1000; w.Admit(S(1000 + (i % 3) * 10, hr: 60), t); }
            PhysioSummary s6 = w.Summarize(t);
            Check("6 rolling window keeps only the last 10 s", s6.NAccepted == 10 && s6.NRaw == 10 && s6.Available && s6.RmssdMs.HasValue && s6.LastDeviceHrBpm == 60,
                  $"acc={s6.NAccepted} raw={s6.NRaw}");
            Check("6b completeness ~ accepted time / window", Math.Abs(s6.Completeness - (s6.MeanIbiMs.Value * 10) / 10000.0) < 1e-6, $"c={s6.Completeness}");
            Check("6c HR from IBI consistent", Math.Abs(s6.HrFromIbiBpm.Value - 60000.0 / s6.MeanIbiMs.Value) < 1e-9);

            // 7. stale snapshot -> null
            PhysioSummary fresh = w.Snapshot(t + 1000, 15000);
            PhysioSummary stale = w.Snapshot(t + 20000, 15000);
            Check("7 fresh snapshot available", fresh != null && fresh.Available);
            Check("7b stale snapshot null (age > maxAge)", stale == null);
            Check("7c window emptied after time passes -> unavailable", !w.Summarize(t + 60000).Available);

            // 8. bridge message parsing: official layout fields
            PpiSample p = PpiSample.Parse("S|12|1|3|28|1234567890|62|830|7|6", 5000, 12.5, "PolarVeritySense/Polar Sense X");
            Check("8 parse bridge sample", p != null && p.PacketSeq == 12 && p.SampleIndex == 1 && p.SamplesInPacket == 3 && p.PacketLength == 28
                  && p.DeviceTimestampNs == 1234567890UL && p.DeviceHrBpm == 62 && p.PpiMs == 830 && p.ErrorEstimateMs == 7
                  && !p.Blocker && p.SkinContact && p.SkinContactSupported && p.ReceiptSessionMs == 5000);
            Check("8b parse rejects malformed", PpiSample.Parse("S|12|1", 0, 0, "x") == null && PpiSample.Parse("X|1|2|3|4|5|6|7|8|9", 0, 0, "x") == null);
            Check("8c flags bit0 blocker", PpiSample.Parse("S|1|0|1|16|0|60|900|3|1", 0, 0, "x").Blocker);

            Console.WriteLine($"\n{_passed} passed, {_failed} failed");
            return _failed == 0 ? 0 : 1;
        }
    }
}
