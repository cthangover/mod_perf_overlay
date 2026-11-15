namespace Cthangover.Mods.PerformanceOverlay
{
    /// <summary>
    /// Serializable window measurements; hitch counters are cumulative for this monitor session.
    /// </summary>
    public sealed class PerfFrameSummary
    {
        /// <summary>
        /// Number of samples retained in the current five-second window; zero identifies an empty startup window.
        /// </summary>
        public int Count { get; set; }

        /// <summary>
        /// Newest retained wall-frame interval in milliseconds; reset to zero when the window is empty.
        /// </summary>
        public double CurrentMs { get; set; }

        /// <summary>
        /// Arithmetic mean of retained wall intervals, used for the displayed FPS estimate rather than engine tick
        /// rate.
        /// </summary>
        public double MeanMs { get; set; }

        /// <summary>
        /// Exact nearest-rank 95th percentile of retained intervals, recomputed only when the monitor refreshes.
        /// </summary>
        public double P95Ms { get; set; }

        /// <summary>
        /// Exact nearest-rank 99th percentile of retained intervals, exposing rare stalls separately from the mean.
        /// </summary>
        public double P99Ms { get; set; }

        /// <summary>
        /// Largest interval in the retained window, distinct from the cumulative session hitch counters.
        /// </summary>
        public double MaxMs { get; set; }

        /// <summary>
        /// Cumulative session intervals above 50 ms; expiring old window samples does not erase this history.
        /// </summary>
        public long Over50 { get; set; }

        /// <summary>
        /// Cumulative session intervals above 250 ms, also included in the broader 50-ms hitch counter.
        /// </summary>
        public long Over250 { get; set; }

        /// <summary>
        /// Session-relative monotonic timestamp of the latest interval above 50 ms; null before any hitch.
        /// </summary>
        public double? LastHitchSeconds { get; set; }

        /// <summary>
        /// Samples displaced by fixed-ring capacity pressure, excluding ordinary five-second window expiration.
        /// </summary>
        public long DroppedSamples { get; set; }
    }
}
