using Cthangover.Core.Mods;
using Cthangover.Core.Utils;
using Godot;

namespace Cthangover.Mods.PerformanceOverlay.Contracts
{
    /// <summary>
    /// Mounts the native monitor driver only for its explicit integration-test selector.
    /// </summary>
    public sealed class PerfOverlayContractInitializer : IModInitializer
    {
        private static bool started;

        /// <summary>
        /// Leaves ordinary mod loading inert; the driver requires completed resource registration.
        /// </summary>
        public void OnModLoaded(string id)
        {
        }

        /// <summary>
        /// Transfers exactly one selected driver to ModRoot's owned deferred queue, preserving normal launches.
        /// </summary>
        public void OnModResourcesReady()
        {
            if (!GameEnvironment.IsRuntime || started)
            {
                return;
            }

            foreach (var arg in OS.GetCmdlineUserArgs())
            {
                if (arg == "--itest=perf_overlay_contracts")
                {
                    started = true;
                    ModRoot.AddDeferred(new PerfOverlayContractDriver());
                }
            }
        }
    }
}
