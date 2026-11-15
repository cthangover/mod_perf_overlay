using System.Collections.Generic;
using Cthangover.Core.Mods.Consumers;

namespace Cthangover.Mods.PerformanceOverlay
{
    /// <summary>
    /// Tracks optional provider instances through mod type-consumer lifetime. Removing a provider before assembly
    /// unload avoids retaining its foreign context.
    /// </summary>
    public sealed class PerfOverlaySectionConsumer : ModTypeConsumer<IPerfOverlaySection>
    {
        private static readonly List<IPerfOverlaySection> providers = new();

        /// <summary>
        /// Stable consumer registry key used during discovery and unload; it is independent of provider mod IDs.
        /// </summary>
        public override string Id
        {
            get
            {
                return "perf_overlay_sections";
            }
        }

        protected override void Add(IPerfOverlaySection value, string mod)
        {
            providers.Add(value);
        }

        protected override void Remove(IPerfOverlaySection value, string mod)
        {
            providers.Remove(value);
        }

        /// <summary>
        /// Visits providers in registration order against the borrowed snapshot at four Hz. Later assignments can
        /// replace the same key; no native resource ownership is transferred.
        /// </summary>
        public static void Collect(PerfOverlaySnapshot snapshot)
        {
            foreach (var provider in providers)
            {
                provider.Collect(snapshot);
            }
        }
    }
}
