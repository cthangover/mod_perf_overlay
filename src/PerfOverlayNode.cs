using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading;
using Cthangover.Core.Bootstrap;
using Cthangover.Core.Events;
using Cthangover.Core.Mods;
using Cthangover.Core.Mods.Testing;
using Cthangover.Core.Scenes;
using Cthangover.Core.Settings;
using Cthangover.Core.Utils;
using Cthangover.Mods.Interface.Base.Lists;
using Cthangover.Mods.Interface.Shell;
using Cthangover.Mods.Interface.View;
using Godot;

namespace Cthangover.Mods.PerformanceOverlay
{
    using System.Linq;
    using Cthangover.Core.Games;

    /// <summary>
    /// Always-processing diagnostics independent of game simulation. Per-frame collection uses numeric fields/fixed
    /// buffers; formatted text and providers run at four Hz.
    /// </summary>
    public partial class PerfOverlayNode : ModWidget
    {
        private CanvasLayer canvas;
        private PanelContainer panel;
        private Label text;
        private PerfFrameGraph graph;
        private readonly PerfFrameWindow frames = new();
        private readonly PerfFrameSummary summary = new();
        private readonly StringBuilder builder = new(8192);
        private string parameters;
        private string quality = "-";
        private readonly long started = Stopwatch.GetTimestamp();
        private long previous;
        private long warningCount;
        private long errorCount;
        private ulong cameraIdentity;
        private double refreshAt;
        private double lastSeconds;
        private double processMs;
        private double physicsMs;
        private double renderMs;
        private double gpuMs;
        private double unaccountedMs;
        private double costTotal;
        private long costFrames;
        private long pipelinePrevious;
        private long pipelineFrame;
        private long pipelineWindow;
        private bool? visibilityOverride;
        private bool initialized;
        private bool measureEnabled;
        private bool captureHidden;
        private PerfRunContext run;
        private PerfCaptureBridge bridge;

        /// <summary>
        /// Borrowed live monitor root for settings, capture coordination and optional providers; cleared when that
        /// root leaves the tree.
        /// </summary>
        public static PerfOverlayNode Current { get; private set; }

        /// <summary>
        /// Latest quarter-second diagnostics object. Serialize immediately when a stable record is needed: the
        /// numeric frame summary is reused by subsequent refreshes.
        /// </summary>
        public PerfOverlaySnapshot Snapshot { get; private set; } = new();

        /// <summary>
        /// Effective visibility policy: an explicit toggle wins, otherwise tests default on and ordinary games
        /// follow the saved enabled setting.
        /// </summary>
        public bool Enabled
        {
            get
            {
                return visibilityOverride ?? (run?.IsTest == true || PerfOverlaySettings.Enabled);
            }
        }

        /// <summary>
        /// Actual diagnostic CanvasLayer visibility, including temporary suppression during capture.
        /// </summary>
        public bool Displayed
        {
            get
            {
                return canvas?.Visible == true;
            }
        }

        /// <summary>
        /// Outcome of the most recent CopySnapshot clipboard attempt, separate from its always-emitted JSON log.
        /// </summary>
        public bool ClipboardCopied { get; private set; }

        /// <summary>
        /// Wall cost of the last enabled process callback, including metric refresh when due. Graph rendering and
        /// native UI rendering are outside this stopwatch.
        /// </summary>
        public double LastCpuMs { get; private set; }

        /// <summary>
        /// Largest measured enabled callback cost since this root was created, including startup and text-refresh
        /// spikes.
        /// </summary>
        public double MaxCpuMs { get; private set; }

        /// <summary>
        /// Mean measured enabled callback cost; disabled frames and separate draw work do not contribute.
        /// </summary>
        public double MeanCpuMs
        {
            get
            {
                return costFrames == 0 ? 0 : costTotal / costFrames;
            }
        }

        /// <summary>
        /// Binds the prefab through the idempotent ModWidget construction gate after its children enter the tree.
        /// </summary>
        public override void _Ready()
        {
            EnsureConstructed();
        }

        protected override void Construct()
        {
            if (!GameEnvironment.IsRuntime)
            {
                return;
            }

            Current = this;
            ProcessMode = ProcessModeEnum.Always;
            ProcessPriority = 10000;
            run = new PerfRunContext(GameLaunchArgs.Startup.UserArgs.ToArray());
            ReadParameters();
            canvas = GetNode<CanvasLayer>("ResourceMonitor");
            panel = GetNode<PanelContainer>("ResourceMonitor/MetricsPanel");
            text = GetNode<Label>("ResourceMonitor/MetricsPanel/Column/MetricsText");
            graph = GetNode<PerfFrameGraph>("ResourceMonitor/MetricsPanel/Column/FrameGraph");
            ViewportCaptureExclusions.Register(canvas);
            GameLogger.LogEmitted += OnLog;
            ModEventBus.Subscribe<SceneEnteredEvent>(SceneChanged, modId: "perf_overlay");
            initialized = true;
            ApplySettings();
            bridge = new PerfCaptureBridge(this);
            ApplicationExit.Preparing += Shutdown;
        }

        private void ReadParameters()
        {
            var result = new StringBuilder();
            foreach (var arg in run.Arguments)
            {
                if (arg.StartsWith("--perf-out=", StringComparison.Ordinal)
                    || arg.StartsWith("--shots=", StringComparison.Ordinal)
                    || arg.StartsWith("--stream-out=", StringComparison.Ordinal))
                {
                    continue;
                }

                if (result.Length > 0)
                {
                    result.Append(' ');
                }

                result.Append(arg);
                if (arg.StartsWith("--quality=", StringComparison.Ordinal)
                    || arg.StartsWith("--preset=", StringComparison.Ordinal))
                {
                    quality = arg.Substring(arg.IndexOf('=') + 1);
                }
            }

            foreach (var key in new[]
            {
                "COMBAT3D_CASE",
                "COMBAT3D_PRESET",
                "ACTOR3D_CASE",
                "ACTOR3D_PRESET",
                "VEGETATION_CASE",
                "VEGETATION_QUALITY"
            })
            {
                var value = System.Environment.GetEnvironmentVariable(key);
                if (string.IsNullOrEmpty(value))
                {
                    continue;
                }

                result.Append(' ').Append(key).Append('=').Append(value);
                if (key.EndsWith("PRESET", StringComparison.Ordinal) || key.EndsWith("QUALITY", StringComparison.Ordinal))
                {
                    quality = value;
                }
            }

            parameters = result.ToString();
        }

        private static string Short(string value, int limit = 80)
        {
            return value.Length <= limit
                ? value
                : value.Substring(0, limit / 2 - 2) + "..."
                    + value.Substring(value.Length - limit / 2 + 1);
        }

        private void SceneChanged(SceneEnteredEvent value)
        {
            refreshAt = 0;
        }

        private void OnLog(string category, string message, LogLevel level)
        {
            if (level == LogLevel.Warning)
            {
                Interlocked.Increment(ref warningCount);
            }

            if (level == LogLevel.Error)
            {
                Interlocked.Increment(ref errorCount);
            }
        }

        /// <summary>
        /// Persists the ordinary-game preference and records an explicit visibility override for this run. The
        /// diagnostic toggle does not capture game input or pause simulation.
        /// </summary>
        public void SetEnabled(bool enabled)
        {
            var values = ModSettingsRegistry.WriteCurrentValues("perf_overlay");
            values.SetBool("enabled", enabled);
            ModSettingsRegistry.ApplyValues("perf_overlay", values);
            ModSettingsRegistry.SaveModSettings("perf_overlay", values);
            visibilityOverride = enabled;
            ApplyVisibility();
        }

        /// <summary>
        /// Reapplies saved settings and test-default visibility, clears the transient toggle override and requests
        /// an immediate quarter-second text refresh.
        /// </summary>
        public void ApplySettings()
        {
            if (!initialized)
            {
                return;
            }

            visibilityOverride = null;
            ApplyVisibility();
            refreshAt = 0;
        }

        private void ApplyVisibility()
        {
            canvas.Visible = Enabled && !captureHidden;
            if (Enabled && !measureEnabled)
            {
                RenderingServer.ViewportSetMeasureRenderTime(GetViewport().GetViewportRid(), true);
                measureEnabled = true;
                previous = 0;
            }
        }

        internal void SetCaptureHidden(bool hidden)
        {
            captureHidden = hidden;
            ApplyVisibility();
        }

        /// <summary>
        /// Observes F3 and Ctrl+F3 for visibility and snapshot copying without claiming the input event from
        /// gameplay.
        /// </summary>
        public override void _Input(InputEvent input)
        {
            if (input is not InputEventKey key || !key.Pressed || key.Echo || key.Keycode != Key.F3)
            {
                return;
            }

            if (key.CtrlPressed)
            {
                CopySnapshot();
            }
            else
            {
                SetEnabled(!Enabled);
            }
        // Diagnostic key observes input; it never claims game movement, focus or pointer ownership.
        }

        /// <summary>
        /// Pumps capture coordination, samples enabled wall/native counters and refreshes metadata at four Hz.
        /// Camera or scene identity changes request an earlier refresh; disabled intervals are not sampled.
        /// </summary>
        public override void _Process(double delta)
        {
            var begin = Stopwatch.GetTimestamp();
            bridge?.Pump();
            if (!Enabled)
            {
                previous = 0;
                return;
            }

            var now = Stopwatch.GetTimestamp();
            lastSeconds = Stopwatch.GetElapsedTime(started, now).TotalSeconds;
            if (previous != 0)
            {
                frames.Add(lastSeconds, Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds);
            }

            previous = now;
            processMs = Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000;
            physicsMs = Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000;
            var viewport = GetViewport();
            var activeCamera = viewport.GetCamera3D();
            var identity = activeCamera?.GetInstanceId() ?? 0;
            if (identity != cameraIdentity)
            {
                cameraIdentity = identity;
                refreshAt = 0;
            }

            renderMs = RenderingServer.ViewportGetMeasuredRenderTimeCpu(viewport.GetViewportRid())
                + RenderingServer.GetFrameSetupTimeCpu();
            gpuMs = RenderingServer.ViewportGetMeasuredRenderTimeGpu(viewport.GetViewportRid());
            var pipelines = (long)(RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.PipelineCompilationsCanvas)
                + RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.PipelineCompilationsMesh)
                + RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.PipelineCompilationsSurface)
                + RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.PipelineCompilationsDraw)
                + RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.PipelineCompilationsSpecialization));
            pipelineFrame = Math.Max(0, pipelines - pipelinePrevious);
            pipelineWindow += pipelineFrame;
            pipelinePrevious = pipelines;
            if (lastSeconds >= refreshAt)
            {
                refreshAt = lastSeconds + .25;
                Refresh();
            }

            LastCpuMs = Stopwatch.GetElapsedTime(begin).TotalMilliseconds;
            MaxCpuMs = Math.Max(MaxCpuMs, LastCpuMs);
            costTotal += LastCpuMs;
            costFrames++;
        }

        private static string T(string id)
        {
            return TranslationServer.Translate("perf_overlay/" + id);
        }

        private static string N(double value)
        {
            return value.ToString("0.00", CultureInfo.InvariantCulture);
        }

        private static double Monitor(Performance.Monitor id)
        {
            return Performance.GetMonitor(id);
        }

        private void Refresh()
        {
            frames.Summarize(lastSeconds, summary);
            var snapshot = new PerfOverlaySnapshot
            {
                Frame = summary
            };
            var v = snapshot.Values;
            var viewport = GetViewport();
            var manager = GetNodeOrNull<SceneManager>("/root/SceneManager");
            var scene = manager?.CurrentSceneName ?? "-";
            var shell = ShellSession.Current?.Kind ?? "frontend";
            v["scene_id"] = scene;
            v["scene_name"] = scene;
            v["shell"] = shell;
            v["location_id"] = "provider pending";
            v["main_scene"] = string.IsNullOrEmpty(GetTree().CurrentScene?.SceneFilePath)
                ? ProjectSettings.GetSetting("application/run/main_scene").AsString()
                : GetTree().CurrentScene.SceneFilePath;
            v["process_ms"] = processMs;
            v["physics_ms"] = physicsMs;
            v["render_cpu_ms"] = renderMs;
            v["gpu_ms"] = gpuMs;
            // These monitors overlap and GPU is pipelined. A positive remainder is a clue,
            // never proof of an idle thread or a blocking call.
            unaccountedMs = Math.Max(0, summary.CurrentMs - Math.Max(processMs, Math.Max(physicsMs, Math.Max(renderMs, gpuMs))));
            v["unaccounted_estimate_ms"] = unaccountedMs;
            v["phase_sum_residual_ms"] = summary.CurrentMs - processMs - physicsMs - renderMs - gpuMs;
            v["static_mib"] = Monitor(Performance.Monitor.MemoryStatic) / 1048576;
            v["video_mib"] = RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.VideoMemUsed) / 1048576.0;
            v["texture_mib"] = RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TextureMemUsed) / 1048576.0;
            v["buffer_mib"] = RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.BufferMemUsed) / 1048576.0;
            v["objects"] = Monitor(Performance.Monitor.ObjectCount);
            v["nodes"] = Monitor(Performance.Monitor.ObjectNodeCount);
            v["orphans"] = Monitor(Performance.Monitor.ObjectOrphanNodeCount);
            v["resources"] = Monitor(Performance.Monitor.ObjectResourceCount);
            v["draw_calls"] = RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalDrawCallsInFrame);
            v["canvas_draw_calls"] = viewport.GetRenderInfo(Viewport.RenderInfoType.Canvas, Viewport.RenderInfo.DrawCallsInFrame);
            v["visible_draw_calls"] = viewport.GetRenderInfo(Viewport.RenderInfoType.Visible, Viewport.RenderInfo.DrawCallsInFrame);
            v["shadow_draw_calls"] = viewport.GetRenderInfo(Viewport.RenderInfoType.Shadow, Viewport.RenderInfo.DrawCallsInFrame);
            v["primitives"] = RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalPrimitivesInFrame);
            v["render_objects"] = RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.TotalObjectsInFrame);
            v["pipeline_frame"] = pipelineFrame;
            v["pipeline_refresh_delta"] = pipelineWindow;
            pipelineWindow = 0;
            v["physics_engine"] = ProjectSettings.GetSetting("physics/3d/physics_engine").AsString();
            v["physics_hz"] = Godot.Engine.PhysicsTicksPerSecond;
            v["max_fps"] = Godot.Engine.MaxFps;
            v["vsync"] = DisplayServer.WindowGetVsyncMode().ToString();
            v["time_scale"] = Godot.Engine.TimeScale;
            v["window_focused"] = GetWindow().HasFocus();
            v["simulation_paused"] = ShellSession.Current?.SimulationPaused ?? GetTree().Paused;
            v["bodies_2d"] = Monitor(Performance.Monitor.Physics2DActiveObjects);
            v["pairs_2d"] = Monitor(Performance.Monitor.Physics2DCollisionPairs);
            v["islands_2d"] = Monitor(Performance.Monitor.Physics2DIslandCount);
            v["bodies_3d"] = Monitor(Performance.Monitor.Physics3DActiveObjects);
            v["pairs_3d"] = Monitor(Performance.Monitor.Physics3DCollisionPairs);
            v["islands_3d"] = Monitor(Performance.Monitor.Physics3DIslandCount);
            v["loaded_mods"] = ModManager.Instance.Mods.Count;
            v["godot"] = Godot.Engine.GetVersionInfo()["string"].AsString();
            v["renderer"] = RenderingServer.GetCurrentRenderingMethod();
            v["driver"] = RenderingServer.GetCurrentRenderingDriverName();
            v["gpu"] = RenderingServer.GetVideoAdapterName();
            v["resolution"] = viewport.GetVisibleRect().Size.ToString();
            v["window_size"] = GetWindow().Size.ToString();
            v["content_scale"] = GetWindow().ContentScaleFactor;
            v["quality"] = quality;
            v["run_parameters"] = parameters;
            if (Boot.ModsReady && ModManager.Instance.Mods.ContainsKey("time_base"))
            {
                v["game_time"] = GameData.Instance.Runtime.Time.Text;
            }
            else
            {
                v["game_time"] = "-";
            }

            var camera = viewport.GetCamera3D();
            var camera2 = viewport.GetCamera2D();
            v["camera"] = camera?.GetPath().ToString() ?? camera2?.GetPath().ToString() ?? "-";
            v["camera_position"] = camera?.GlobalPosition.ToString() ?? camera2?.GlobalPosition.ToString() ?? "-";
            v["camera_parent_distance_m"] = camera == null ? 0 : camera.Position.Length();
            v["warnings"] = Interlocked.Read(ref warningCount);
            v["errors"] = Interlocked.Read(ref errorCount);
            v["session_seconds"] = lastSeconds;
            v["overlay_cpu_mean_ms"] = MeanCpuMs;
            v["overlay_cpu_max_ms"] = MaxCpuMs;
            var progress = ModTestProgress.Current;
            v["test_mode"] = run.Mode;
            v["test_suite"] = progress?.ModId ?? run.Suite;
            v["test_id"] = progress?.TestId ?? "scene driver";
            v["test_index"] = progress?.Index;
            v["test_total"] = progress?.Total;
            v["test_seconds"] = progress?.CaseSeconds;
            v["suite_seconds"] = progress?.SuiteSeconds;
            v["arguments"] = run.Arguments;
            PerfOverlaySectionConsumer.Collect(snapshot);
            Snapshot = snapshot;
            builder.Clear();
            AppendTestProgress(v, progress);
            builder.Append(scene).Append(" · ").Append(shell).Append(" · ").Append(v["physics_engine"]).AppendLine();
            AppendFrameStatistics(v);
            if (PerfOverlaySettings.Detailed)
            {
                AppendDetailed(v);
            }

            foreach (var section in snapshot.Sections)
            {
                builder.Append(section.Key).Append(": ").Append(section.Value).AppendLine();
            }

            builder.Append(T("hint"));
            text.Text = builder.ToString().Replace("\r\n", "\n");
            graph.Refresh(frames);
            Place();
        }

        private void AppendTestProgress(Dictionary<string, object> v, ModTestProgress progress)
        {
            if (run.IsTest || progress != null)
            {
                builder.Append(T("test")).Append(' ').Append(v["test_suite"]).Append(" / ").Append(v["test_id"]).Append(" · ")
                    .Append(progress == null ? "?/?" : progress.Index + "/" + progress.Total)
                    .Append(" · ")
                    .Append(run.Mode)
                    .Append(" · ")
                    .Append(N(progress?.CaseSeconds ?? lastSeconds))
                    .Append("s / ")
                    .Append(N(lastSeconds))
                    .AppendLine("s");
            }

            if (run.IsTest || progress != null)
            {
                builder.Append(T("parameters")).Append(' ').Append(Short(parameters)).AppendLine();
            }
        }

        private void AppendFrameStatistics(Dictionary<string, object> v)
        {
            builder.Append("FPS ").Append(N(summary.MeanMs > 0 ? 1000 / summary.MeanMs : 0)).Append(" · ms ").Append(N(summary.CurrentMs))
                .Append(" / avg ")
                .Append(N(summary.MeanMs))
                .Append(" / p95 ")
                .Append(N(summary.P95Ms))
                .AppendLine();
            builder.Append("p99 ").Append(N(summary.P99Ms)).Append(" / max ").Append(N(summary.MaxMs)).Append(" · >50 ")
                .Append(summary.Over50)
                .Append(" / >250 ")
                .Append(summary.Over250)
                .Append(" · @")
                .Append(summary.LastHitchSeconds.HasValue ? N(summary.LastHitchSeconds.Value) : "-")
                .AppendLine("s");
            builder.Append("CPU proc/phys/render ").Append(N(processMs)).Append('/').Append(N(physicsMs)).Append('/')
                .Append(N(renderMs))
                .Append(" · GPU ")
                .Append(N(gpuMs))
                .AppendLine(" ms");
            builder.Append(T("unaccounted")).Append(' ').Append(N(unaccountedMs)).Append(" ms · ").Append(T("overlap"))
                .AppendLine();
            builder.Append("wall - Σ ").Append(N((double)v["phase_sum_residual_ms"])).AppendLine(" ms");
            builder.Append("MiB static/video/tex/buf ").Append(N((double)v["static_mib"])).Append('/').Append(N((double)v["video_mib"]))
                .Append('/')
                .Append(N((double)v["texture_mib"]))
                .Append('/')
                .Append(N((double)v["buffer_mib"]))
                .AppendLine();
            builder.Append("draw ").Append(v["draw_calls"]).Append(" · primitives ").Append(v["primitives"]).Append(" · pipeline Δ ")
                .Append(v["pipeline_refresh_delta"])
                .Append(" · W/E ")
                .Append(v["warnings"])
                .Append('/')
                .Append(v["errors"])
                .AppendLine();
        }

        private void AppendDetailed(Dictionary<string, object> v)
        {
            builder.Append("objects/nodes/orphans/resources ").Append(v["objects"]).Append('/').Append(v["nodes"])
                .Append('/')
                .Append(v["orphans"])
                .Append('/')
                .Append(v["resources"])
                .AppendLine();
            builder.Append("physics 2D bodies/pairs/islands ").Append(v["bodies_2d"]).Append('/').Append(v["pairs_2d"])
                .Append('/')
                .Append(v["islands_2d"])
                .Append(" · 3D ")
                .Append(v["bodies_3d"])
                .Append('/')
                .Append(v["pairs_3d"])
                .Append('/')
                .Append(v["islands_3d"])
                .AppendLine();
            builder.Append(v["renderer"]).Append('/').Append(v["driver"]).Append(" · ").Append(v["resolution"])
                .Append(" scale ")
                .Append(v["content_scale"])
                .Append(" · ")
                .Append(v["physics_hz"])
                .AppendLine(" Hz");
            builder.Append("cap ").Append(v["max_fps"]).Append(" · VSync ").Append(v["vsync"]).Append(" · focus ")
                .Append(v["window_focused"])
                .Append(" · paused ")
                .Append(v["simulation_paused"])
                .Append(" · scale ")
                .Append(v["time_scale"])
                .AppendLine();
            builder.Append(v["gpu"]).Append(" · Godot ").Append(v["godot"]).AppendLine();
            builder.Append("mods ").Append(v["loaded_mods"]).Append(" · time ").Append(v["game_time"]).AppendLine();
            builder.Append("location ").Append(v["location_id"]).Append(" · quality ").Append(v["quality"])
                .AppendLine();
            builder.Append("camera ").Append(Short(v["camera"].ToString(), 64)).AppendLine();
            builder.Append(" @ ").Append(v["camera_position"]).Append(" parent distance ").Append(v["camera_parent_distance_m"])
                .AppendLine();
            builder.Append("main ").Append(Short(v["main_scene"].ToString())).AppendLine();
            builder.Append(T("parameters")).Append(' ').Append(Short(parameters)).AppendLine();
            builder.Append("overlay CPU avg/max ").Append(N(MeanCpuMs)).Append('/').Append(N(MaxCpuMs)).AppendLine(" ms");
        }

        private void Place()
        {
            var size = GetViewport().GetVisibleRect().Size;
            panel.ResetSize();
            var desired = panel.GetCombinedMinimumSize();
            panel.Size = desired;
            panel.Position = new Vector2(
                PerfOverlaySettings.Corner.EndsWith("right", StringComparison.Ordinal) ? Math.Max(8, size.X - desired.X - 12) : 12,
                PerfOverlaySettings.Corner.StartsWith("bottom", StringComparison.Ordinal) ? Math.Max(8, size.Y - desired.Y - 12) : 12
            );
        }

        /// <summary>
        /// The settings action and Ctrl+F3 copy exactly the current visible values, without multiline log records.
        /// </summary>
        public string CopySnapshot()
        {
            var json = JsonSerializer.Serialize(Snapshot);
            ClipboardCopied = PerfOverlayClipboard.TrySet(json);
            GameLogger.Log("PERF_OVERLAY", json);
            if (!ClipboardCopied)
            {
                GameLogger.Log("PERF_OVERLAY", T("copy_busy"), LogLevel.Warning);
            }

            return json;
        }

        private async System.Threading.Tasks.Task Shutdown(SceneTree tree)
        {
            if (bridge != null)
            {
                await bridge.Stop();
            }
        }

        protected override void Destruct()
        {
            ApplicationExit.Preparing -= Shutdown;
            bridge?.Close();
            bridge = null;
            GameLogger.LogEmitted -= OnLog;
            ModEventBus.Unsubscribe<SceneEnteredEvent>(SceneChanged);
            ViewportCaptureExclusions.Unregister(canvas);
            if (GodotObject.IsInstanceValid(canvas))
            {
                canvas.Free();
            }

            // Timing ownership is shared with profilers; retain its enabled state instead of disabling someone else's instrumentation.
            if (Current == this)
            {
                Current = null;
            }
        }
    }
}
