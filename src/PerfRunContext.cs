using System;
using System.Collections.Generic;

namespace Cthangover.Mods.PerformanceOverlay
{
    /// <summary>
    /// Command-line identity is a fallback for scene drivers without ModTest methods. A missing method/count stays
    /// explicitly unknown rather than a made-up test ID.
    /// </summary>
    public sealed class PerfRunContext
    {
        /// <summary>
        /// Whether recognized unit, integration, demo or matrix arguments require default-on diagnostic visibility.
        /// </summary>
        public bool IsTest { get; }

        /// <summary>
        /// Best available command-line suite identity, used only when the core has no currently running ModTest.
        /// </summary>
        public string Suite { get; } = "-";

        /// <summary>
        /// Run-mode precedence is matrix, demo, unit, contract, then game; it labels diagnostics without
        /// controlling execution.
        /// </summary>
        public string Mode { get; } = "game";

        /// <summary>
        /// Startup clone of the original user arguments, retained for unfiltered JSON provenance and display
        /// formatting.
        /// </summary>
        public string[] Arguments { get; }

        /// <summary>
        /// Captures a private argument clone and classifies run mode without invoking Godot. Missing runner
        /// IDs/counts remain unknown rather than fabricated.
        /// </summary>
        public PerfRunContext(string[] arguments)
        {
            Arguments = (string[])arguments.Clone();
            var demo = false;
            var unit = false;
            var contract = false;
            var matrix = false;
            foreach (var arg in arguments)
            {
                if (arg.StartsWith("--itest=", StringComparison.Ordinal))
                {
                    Suite = arg.Substring(8);
                    contract = true;
                }

                if (arg == "--run-tests")
                {
                    unit = true;
                }

                if (arg.StartsWith("--mod=", StringComparison.Ordinal) && Suite == "-")
                {
                    Suite = arg.Substring(6);
                }

                if (arg.StartsWith("--", StringComparison.Ordinal)
                    && arg.Split('=')[0].Contains("demo", StringComparison.OrdinalIgnoreCase))
                {
                    demo = true;
                }

                if (arg.StartsWith("--matrix", StringComparison.Ordinal))
                {
                    matrix = true;
                }

                if (arg.StartsWith("--choices=", StringComparison.Ordinal)
                    || arg.StartsWith("--timeout=", StringComparison.Ordinal))
                {
                    contract = true;
                    if (Suite == "-")
                    {
                        Suite = "dialog_base__autoplay";
                    }
                }

                if (arg.StartsWith("--test=", StringComparison.Ordinal)
                    || arg.StartsWith("--test-filter=", StringComparison.Ordinal))
                {
                    contract = true;
                }
            }

            IsTest = matrix || demo || unit || contract;
            Mode = matrix ? "matrix" : demo ? "demo" : unit ? "unit" : contract ? "contract" : "game";
        }
    }
}
