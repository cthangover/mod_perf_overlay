using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Cthangover.Core.Mods.HotReload;
using Cthangover.Core.Utils;
using Godot;

namespace Cthangover.Mods.PerformanceOverlay
{
    /// <summary>
    /// Publishes the monitor's temporary controls and latest measurements through the mod command registry.
    /// Both handlers run on the game thread, where the monitor's borrowed snapshot can be copied safely.
    /// </summary>
    public sealed class PerfDevelopmentCommand : IModDevelopmentCommand
    {
        private static readonly string[] Metrics =
        {
            "scene_id", "location_id", "shell", "camera", "process_ms", "physics_ms", "max_fps", "vsync",
            "renderer", "driver", "resolution", "window_size", "render_scale_3d", "content_scale", "quality",
            "physics_hz", "time_scale", "window_focused", "simulation_paused", "static_mib",
            "overlay_cpu_mean_ms", "overlay_cpu_max_ms"
        };

        /// <summary>Registers sampling controls and a separate read-only statistics query.</summary>
        public IReadOnlyCollection<string> Commands { get; } = new[] { "perf.configure", "perf.read" };

        /// <summary>Describes the settings that can change independently and the data available to an agent.</summary>
        public string Description => "perf.configure: set enabled (sampling), overlay (visibility), or reset the five-second window without saving preferences. perf.read: return the latest frame, CPU/GPU and scene snapshot, with freshness and native timing availability.";

        /// <summary>Updates transient controls when requested and returns a detached snapshot of the live monitor.</summary>
        public Task<JsonNode> ExecuteAsync(ModDevelopmentContext context, string command, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var monitor = PerfOverlayNode.Current;
            if (!GameEnvironment.IsRuntime || !GodotObject.IsInstanceValid(monitor))
                throw new InvalidOperationException("The performance overlay is not ready.");

            if (command == "perf.configure")
            {
                var enabled = OptionalFlag(context.Request, "enabled");
                var overlay = OptionalFlag(context.Request, "overlay");
                var reset = OptionalFlag(context.Request, "reset") == true;
                if (monitor.ConfigureMeasurement(enabled, overlay, reset))
                    context.MarkChanged();
            }

            return Task.FromResult<JsonNode>(Read(monitor));
        }

        private static bool? OptionalFlag(JsonObject request, string name)
        {
            if (!request.TryGetPropertyValue(name, out var option))
                return null;
            if (option is JsonValue value && value.TryGetValue<bool>(out var flag))
                return flag;
            throw new ArgumentException(name + " must be a boolean.");
        }

        private static JsonObject Read(PerfOverlayNode monitor)
        {
            var snapshot = monitor.Snapshot;
            var frame = snapshot.Frame;
            var age = monitor.SnapshotAgeMs;
            var values = snapshot.Values;
            var metrics = new JsonObject();
            foreach (var name in Metrics)
                metrics[name] = Metric(values, name);

            metrics["render_cpu_ms"] = NativeTime(values, "render_cpu_ms", monitor.NativeRendering);
            metrics["gpu_ms"] = NativeTime(values, "gpu_ms", monitor.NativeRendering);
            metrics["draw_calls"] = monitor.NativeRendering ? Metric(values, "draw_calls") : null;
            metrics["video_mib"] = monitor.NativeRendering ? Metric(values, "video_mib") : null;
            return new JsonObject
            {
                ["enabled"] = monitor.Sampling,
                ["overlay"] = monitor.Enabled,
                ["displayed"] = monitor.Displayed,
                ["window_id"] = monitor.WindowId,
                ["state"] = frame.Count == 0 ? "empty" : monitor.Sampling ? "sampling" : "stopped",
                ["snapshot_age_ms"] = age,
                ["fresh"] = monitor.Sampling && monitor.CurrentSnapshot && frame.Count > 0 && age <= 1000,
                ["renderer_timings_available"] = metrics["render_cpu_ms"] != null || metrics["gpu_ms"] != null,
                ["native_timing_enabled"] = monitor.NativeTimingEnabled,
                ["frame"] = new JsonObject
                {
                    ["count"] = frame.Count,
                    ["window_seconds"] = 5,
                    ["mean_fps"] = frame.MeanMs > 0 ? 1000 / frame.MeanMs : 0,
                    ["current_ms"] = frame.CurrentMs,
                    ["mean_ms"] = frame.MeanMs,
                    ["p95_ms"] = frame.P95Ms,
                    ["p99_ms"] = frame.P99Ms,
                    ["max_ms"] = frame.MaxMs,
                    ["over_50_ms"] = frame.Over50,
                    ["over_250_ms"] = frame.Over250,
                    ["last_hitch_seconds"] = frame.LastHitchSeconds,
                    ["dropped_samples"] = frame.DroppedSamples
                },
                ["metrics"] = metrics
            };
        }

        private static JsonNode Metric(Dictionary<string, object> values, string name)
        {
            return values.TryGetValue(name, out var value) ? JsonSerializer.SerializeToNode(value) : null;
        }

        private static JsonNode NativeTime(Dictionary<string, object> values, string name, bool nativeRendering)
        {
            return nativeRendering && values.TryGetValue(name, out var value)
                && value is double milliseconds && double.IsFinite(milliseconds) && milliseconds > 0
                ? JsonValue.Create(milliseconds) : null;
        }
    }
}
