// File: PpiSample.cs
// One raw pulse-to-pulse interval sample as delivered by the Polar PMD stream, stamped with
// Unity receipt time. Plain C#: no Unity dependency, so it compiles in the headless tests.

using System;
using System.Globalization;

namespace AdaptiveAR.Physiology
{
    public sealed class PpiSample
    {
        // --- receipt (Unity side) ---
        /// <summary>Session clock at receipt, milliseconds (-1 before a session file is open).</summary>
        public double ReceiptSessionMs;
        /// <summary>Monotonic clock at receipt, seconds (Time.realtimeSinceStartup).</summary>
        public double ReceiptRealtimeS;

        // --- device fields (raw) ---
        /// <summary>PMD frame timestamp, nanoseconds since 2000-01-01T00:00:00Z on the sensor clock; 0 = not provided.</summary>
        public ulong DeviceTimestampNs;
        public int PpiMs;
        public int DeviceHrBpm;
        public int ErrorEstimateMs;
        public bool Blocker;
        public bool SkinContact;
        public bool SkinContactSupported;

        // --- framing ---
        public long PacketSeq;
        public int SampleIndex;
        public int SamplesInPacket;
        public int PacketLength;
        public string Source;

        // --- live-window decision (set by the physiology layer; the raw record is logged either way) ---
        public bool Accepted;
        public string RejectReason;

        /// <summary>
        /// Parses the bridge message "S|seq|idx|n|len|tsNs|hr|ppi|err|flags". Returns null on any
        /// malformed field (the caller logs the raw text).
        /// </summary>
        public static PpiSample Parse(string message, double receiptSessionMs, double receiptRealtimeS, string source)
        {
            if (string.IsNullOrEmpty(message)) return null;
            string[] p = message.Split('|');
            if (p.Length < 10 || p[0] != "S") return null;
            try
            {
                var c = CultureInfo.InvariantCulture;
                int flags = int.Parse(p[9], c);
                return new PpiSample
                {
                    ReceiptSessionMs = receiptSessionMs,
                    ReceiptRealtimeS = receiptRealtimeS,
                    PacketSeq = long.Parse(p[1], c),
                    SampleIndex = int.Parse(p[2], c),
                    SamplesInPacket = int.Parse(p[3], c),
                    PacketLength = int.Parse(p[4], c),
                    DeviceTimestampNs = ulong.Parse(p[5], c),
                    DeviceHrBpm = int.Parse(p[6], c),
                    PpiMs = int.Parse(p[7], c),
                    ErrorEstimateMs = int.Parse(p[8], c),
                    Blocker = (flags & 0x01) != 0,
                    SkinContact = (flags & 0x02) != 0,
                    SkinContactSupported = (flags & 0x04) != 0,
                    Source = source
                };
            }
            catch (Exception) { return null; }
        }
    }
}
