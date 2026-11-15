namespace Cthangover.Mods.PerformanceOverlay
{
    /// <summary>
    /// Optional auto-discovered quarter-second contributor. Add namespaced JSON fields/visible rows; never advance
    /// simulation or retain foreign native resources.
    /// </summary>
    public interface IPerfOverlaySection
    {
        /// <summary>
        /// Adds JSON-compatible values and visible section rows to the borrowed quarter-second snapshot. Providers
        /// must not advance gameplay, retain foreign native objects or claim input ownership.
        /// </summary>
        void Collect(PerfOverlaySnapshot snapshot);
    }
}
