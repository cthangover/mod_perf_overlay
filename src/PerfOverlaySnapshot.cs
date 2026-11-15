using System.Collections.Generic;

namespace Cthangover.Mods.PerformanceOverlay
{
    /// <summary>
    /// All visible diagnostics are serializable in one line. Missing/unsupported values remain explicit; phase
    /// timings overlap and are not additive.
    /// </summary>
    public sealed class PerfOverlaySnapshot
    {
        /// <summary>
        /// Serializable rolling frame summary; the live monitor reuses it, so retain serialized JSON for a frozen
        /// record.
        /// </summary>
        public PerfFrameSummary Frame { get; set; } = new();

        /// <summary>
        /// Named JSON-compatible diagnostics shared by visible formatting, clipboard export and optional providers.
        /// Missing platform capabilities remain explicit values rather than native resource references.
        /// </summary>
        public Dictionary<string, object> Values { get; set; } = new();

        /// <summary>
        /// Provider-owned row identifiers and text displayed after the common diagnostics, in insertion order.
        /// </summary>
        public Dictionary<string, string> Sections { get; set; } = new();

        /// <summary>
        /// Adds or replaces a named provider row in this snapshot without retaining the provider or advancing its
        /// state.
        /// </summary>
        public void Section(string id, string value)
        {
            Sections[id] = value;
        }
    }
}
