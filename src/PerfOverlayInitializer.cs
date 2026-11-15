using System;
using Cthangover.Core.Mods;
using Cthangover.Core.Scenes;
using Cthangover.Core.Utils;
using Godot;

namespace Cthangover.Mods.PerformanceOverlay
{
    /// <summary>
    /// Creates one live monitor from its mod-aware prefab after resources are registered.
    /// The scene resolver owns cached prototypes; this initializer owns only the live root.
    /// </summary>
    public sealed class PerfOverlayInitializer : IModInitializer, IModUnloadAware
    {
        private const string ScenePath = "scenes/ui/perf_overlay.tscn";
        private static PerfOverlayNode node;

        /// <summary>
        /// Defers scene construction until the loader has registered scripts and resources.
        /// </summary>
        public void OnModLoaded(string id)
        {
        }

        /// <summary>
        /// Resolves the prefab once and transfers its detached live tree to ModRoot's owned deferred queue.
        /// Repeated resource notifications preserve the existing monitor.
        /// </summary>
        public void OnModResourcesReady()
        {
            if (!GameEnvironment.IsRuntime || GodotObject.IsInstanceValid(node))
            {
                return;
            }

            node = TscnScenes.LoadAndBuild(ScenePath) as PerfOverlayNode
                ?? throw new InvalidOperationException("Resource monitor prefab is unavailable");
            ModRoot.AddDeferred(node);
        }

        /// <summary>
        /// Releases the live control and its capture/log subscriptions before its assembly leaves.
        /// </summary>
        public void OnModUnloading()
        {
            if (GodotObject.IsInstanceValid(node))
            {
                node.Free();
            }

            node = null;
        }

        /// <summary>
        /// Completes the loader contract; all native ownership has already ended in OnModUnloading.
        /// </summary>
        public void OnModUnloaded(string id)
        {
        }
    }
}
