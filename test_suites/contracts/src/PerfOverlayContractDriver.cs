using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Cthangover.Core.Bootstrap;
using Cthangover.Core.Mods;
using Cthangover.Core.Mods.Resolvers;
using Cthangover.Core.Mods.Testing;
using Cthangover.Core.Scenes;
using Cthangover.Core.Settings;
using Cthangover.Core.Utils;
using Cthangover.Mods.Interface.Shell;
using Cthangover.Mods.Interface.View;
using Cthangover.Mods.Scene3d;
using Godot;

namespace Cthangover.Mods.PerformanceOverlay.Contracts
{
    /// <summary>
    /// Exercises the live overlay across both shell kinds, clipboard, capture exclusion and current-ModTest
    /// metadata. The driver owns its temporary 3D probe resources.
    /// </summary>
    public partial class PerfOverlayContractDriver : Node
    {
        private string output;
        private int checks;
        private readonly JsonObject row = new();
        private MeshInstance3D sphere;
        private SphereMesh mesh;
        private StandardMaterial3D material;
        private static string Arg(string prefix)
        {
            foreach (var arg in OS.GetCmdlineUserArgs())
            {
                if (arg.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return arg.Substring(prefix.Length);
                }
            }

            return "";
        }

        private void Check(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }

            checks++;
        }

        private async Task Frames(int count)
        {
            for (var i = 0; i < count; i++)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
        }

        private async Task Enter(string scene, string kind)
        {
            var manager = GetTree().Root.GetNode<SceneManager>("SceneManager");
            if (ShellSession.Current == null)
            {
                manager.PendingSceneName = scene;
                GetTree().Root.GetNode<GodotSceneService>("GodotSceneService").LoadShell();
            }
            else
            {
                manager.SwitchScene(scene);
            }

            var watch = Stopwatch.StartNew();
            while (watch.Elapsed.TotalSeconds < 30)
            {
                if (ShellSession.Current?.Ready == true && ShellSession.Current.Kind == kind
                    && GetTree().Root.GetNode<SceneManager>("SceneManager").CurrentSceneName == scene)
                {
                    await Frames(3);
                    return;
                }

                await Frames(1);
            }

            throw new TimeoutException("Shell transition " + scene);
        }

        /// <summary>
        /// Runs the bounded native contract, writes its actual counters and requests coordinated ApplicationExit. A
        /// host-side audit additionally checks complete logs and the foreground capture attempt.
        /// </summary>
        public override async void _Ready()
        {
            ProcessMode = ProcessModeEnum.Always;
            var code = 0;
            output = Arg("--perf-out=");
            try
            {
                Directory.CreateDirectory(output);
                await Frames(8);
                var monitor = PerfOverlayNode.Current;
                Check(monitor != null && monitor.Enabled && monitor.Displayed, "Monitor must default on in tests");
                var clipboardWait = Stopwatch.StartNew();
                string initialClipboard;
                while (!PerfOverlayClipboard.TryGet(out initialClipboard) && clipboardWait.Elapsed.TotalSeconds < 3)
                {
                    await Frames(1);
                }

                Check(initialClipboard != null, "Readable clipboard before copy");
                try
                {
                    var snapshot = monitor.CopySnapshot();
                    string copiedClipboard;
                    Check(
                        monitor.ClipboardCopied && PerfOverlayClipboard.TryGet(out copiedClipboard)
                        && copiedClipboard == snapshot,
                        "Copy snapshot clipboard"
                    );
                    using var document = JsonDocument.Parse(snapshot);
                    Check(
                        document.RootElement.GetProperty("Values").GetProperty("test_suite").GetString()
                        == "perf_overlay_contracts",
                        "Suite identity"
                    );
                }
                finally
                {
                    clipboardWait.Restart();
                    var restored = false;
                    while (!(restored = PerfOverlayClipboard.TrySet(initialClipboard))
                        && clipboardWait.Elapsed.TotalSeconds < 3)
                    {
                        await Frames(1);
                    }

                    Check(restored, "Previous clipboard restored");
                }

                using (var key = new InputEventKey
                {
                    Keycode = Key.F3,
                    Pressed = true
                }

                )
                {
                    Input.ParseInputEvent(key);
                }

                await Frames(2);
                Check(!monitor.Displayed, "F3 disables");
                using (var key = new InputEventKey
                {
                    Keycode = Key.F3,
                    Pressed = true
                }

                )
                {
                    Input.ParseInputEvent(key);
                }

                await Frames(2);
                Check(monitor.Displayed, "F3 enables");
                await Enter("perf_overlay_2d", "2d");
                await Locale("ru");
                var settings = new DataBlob();
                settings.SetBool("detailed", true);
                settings.SetString("corner", "top_right");
                ModSettingsRegistry.ApplyValues("perf_overlay", settings);
                await Frames(30);
                Check(monitor.Displayed, "Test auto visibility survives settings initialization");
                Check((string)monitor.Snapshot.Values["shell"] == "2d", "2D metadata");
                await Capture("2d_ru");
                await Enter("perf_overlay_3d", "3d");
                var host = Scene3dHost.Current;
                await host.Loading;
                Check(host.Ready, "3D host");
                mesh = new SphereMesh
                {
                    Radius = .8f,
                    Height = 1.6f
                };
                material = new StandardMaterial3D
                {
                    AlbedoColor = new Color(.15f, .6f, .3f)
                };
                sphere = new MeshInstance3D
                {
                    Mesh = mesh,
                    MaterialOverride = material,
                    Position = Vector3.Up
                };
                host.LocalNode("").AddChild(sphere);
                host.GetViewport().GetCamera3D().LookAt(Vector3.Up);
                await Locale("en");
                await Frames(30);
                Check((string)monitor.Snapshot.Values["shell"] == "3d", "3D metadata");
                Check(monitor.Snapshot.Values["camera"].ToString() != "-", "3D camera metadata");
                await Capture("3d_en");
                foreach (var corner in new[]
                {
                    "top_left",
                    "top_right",
                    "bottom_left",
                    "bottom_right"
                }

                )
                {
                    settings.SetString("corner", corner);
                    ModSettingsRegistry.ApplyValues("perf_overlay", settings);
                    await Frames(3);
                    Check(PerfOverlaySettings.Corner == corner, "Settings corner " + corner);
                }

                settings.SetBool("detailed", false);
                ModSettingsRegistry.ApplyValues("perf_overlay", settings);
                await Frames(5);
                Check(!PerfOverlaySettings.Detailed, "Compact mode");
                await ModTestRunner.CaptureRootBaselineAsync();
                var mod = (ModInfo)ModManager.Instance.GetMod("perf_overlay__contracts");
                var assembly = ModTestEngine.CompileTestAssembly(mod, CancellationToken.None);
                Check(assembly != null, "Current-case test compiled");
                var ledger = new ModTestLedger();
                var suite = await ModTestRunner.RunSuiteAsync(mod.Id, assembly, ledger, CancellationToken.None);
                Check(suite.Passed == 1 && suite.Failed == 0 && suite.Errors == 0, "Read-only current-case native contract");
                Check(ModTestProgress.Current == null, "Current case clears after runner exit");
                await Measure(monitor);
                Check(monitor.Snapshot.Frame.Count > 0, "Nonempty frame statistics");
                row["snapshot"] = JsonSerializer.SerializeToNode(monitor.Snapshot);
                row["status"] = "OK";
            }
            catch (Exception error)
            {
                code = 1;
                row["status"] = "FAIL";
                row["error"] = error.ToString();
                GameLogger.Log("PERF_OVERLAY", error.ToString(), LogLevel.Error);
            }
            finally
            {
                if (GodotObject.IsInstanceValid(sphere))
                {
                    sphere.Free();
                }

                mesh?.Dispose();
                material?.Dispose();
            }

            row["checks"] = checks;
            File.WriteAllText(Path.Combine(output, "row.json"), row.ToJsonString(new() { WriteIndented = true }));
            GameLogger.Log("TEST", "[TEST_RESULT] name=perf_overlay passed=" + (code == 0 ? 1 : 0) + " failed=" + (code == 0 ? 0 : 1));
            ApplicationExit.Quit(GetTree(), code);
        }

        private async Task Locale(string language)
        {
            GameData.Instance.Settings.Language = language;
            Cthangover.Core.Localization.LocaleLoader.LoadLocale(language);
            await Frames(3);
        }

        private async Task Capture(string name)
        {
            var panel = PerfOverlayNode.Current.GetNode<Control>("ResourceMonitor/MetricsPanel");
            var bounds = panel.GetGlobalRect();
            var visibleBounds = GetViewport().GetVisibleRect();
            Check(
                bounds.Position.X >= visibleBounds.Position.X && bounds.Position.Y >= visibleBounds.Position.Y
                && bounds.End.X <= visibleBounds.End.X
                && bounds.End.Y <= visibleBounds.End.Y,
                "Monitor fits the viewport in " + name
            );
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            Check(
                ViewportCapture.SavePng(GetViewport(), Path.Combine(output, name + "_overlay.png"), true),
                "Overlay documentation capture"
            );
            using var clean = ViewportCapture.Take(GetViewport());
            Check(clean != null, "Clean image");
            clean.SavePng(Path.Combine(output, name + "_game.png"));
            Check(PerfOverlayNode.Current.Displayed, "Visibility restored after capture");
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            using var visible = ViewportCapture.Take(GetViewport(), true);
            var a = clean.GetData();
            var b = visible.GetData();
            var different = 0;
            for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
            {
                if (a[i] != b[i])
                {
                    different++;
                }
            }

            Check(different > 2000, "Capture must exclude overlay pixels");
            row[name + "_excluded_bytes"] = different;
            if (name == "3d_en")
            {
                GetWindow().GrabFocus();
                File.WriteAllText(
                    Path.Combine(output, "display-check.ready"),
                    "Native window capture must hide the monitor through its pipe and restore afterwards"
                );
                var timer = Stopwatch.StartNew();
                while (!File.Exists(Path.Combine(output, "display-check.done")) && timer.Elapsed.TotalSeconds < 15)
                {
                    await Frames(1);
                }

                Check(File.Exists(Path.Combine(output, "display-check.done")), "Host capture attempt completed");
            }
        }

        private async Task Measure(PerfOverlayNode monitor)
        {
            var samples = new List<double>();
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed.TotalSeconds < 6 || samples.Count < 1000)
            {
                await Frames(1);
                samples.Add(monitor.LastCpuMs);
                if (samples.Count > 30000)
                {
                    break;
                }
            }

            File.WriteAllText(Path.Combine(output, "overlay_cpu_samples.json"), JsonSerializer.Serialize(samples));
            samples.Sort();
            row["overlay_cpu_mean_ms"] = monitor.MeanCpuMs;
            row["overlay_cpu_p95_ms"] = samples[(int)(samples.Count * .95)];
            row["overlay_cpu_p99_ms"] = samples[(int)(samples.Count * .99)];
            row["overlay_cpu_max_ms"] = samples[samples.Count - 1];
            row["samples"] = samples.Count;
            row["seconds"] = timer.Elapsed.TotalSeconds;
        }
    }
}
