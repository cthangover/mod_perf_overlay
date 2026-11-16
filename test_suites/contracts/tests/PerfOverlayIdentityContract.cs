using System.Threading.Tasks;
using Cthangover.Core.Mods.Testing;
using Godot;

namespace Cthangover.Mods.PerformanceOverlay.Contracts
{
    /// <summary>
    /// Verifies the core's read-only current-case contract through the actual mod runner and visible snapshot.
    /// </summary>
    public static class PerfOverlayIdentityContract
    {
        /// <summary>
        /// Checks exact suite, method and one-of-one progress while the runner owns the case, then captures the
        /// same identity shown by the live overlay.
        /// </summary>
        [ModTest("monitor/current-case", TimeoutSeconds = 15)]
        public static async Task CurrentCase(ModTestContext context)
        {
            var progress = ModTestProgress.Current;
            ModAssert.NotNull(progress, "Running case identity");
            ModAssert.Equal(context.ModId, progress.ModId);
            ModAssert.Equal("monitor/current-case", progress.TestId);
            ModAssert.Equal(1, progress.Index);
            ModAssert.Equal(1, progress.Total);
            var tree = (SceneTree)Godot.Engine.GetMainLoop();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (watch.Elapsed.TotalSeconds < .4)
            {
                await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            }

            var visible = PerfOverlayNode.Current.Snapshot.Values;
            ModAssert.Equal("monitor/current-case", (string)visible["test_id"]);
            ModAssert.Equal(1, (int)visible["test_index"]);
            foreach (var arg in OS.GetCmdlineUserArgs())
            {
                if (arg.StartsWith("--perf-out=", System.StringComparison.Ordinal))
                {
                    await tree.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                    ModAssert.True(
                        Cthangover.Mods.Interface.View.ViewportCapture.SavePng(
                            tree.Root,
                            System.IO.Path.Combine(arg.Substring(11), "current_modtest_en_overlay.png"),
                            true),
                        "Current ModTest documentation capture"
                    );
                }
            }
        }
    }
}
