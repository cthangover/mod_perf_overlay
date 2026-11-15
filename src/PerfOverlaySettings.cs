using System.Collections.Generic;
using Cthangover.Core.Mods;
using Cthangover.Core.Settings;
using Godot;

namespace Cthangover.Mods.PerformanceOverlay
{
    /// <summary>
    /// Bridges persisted mod settings to the live monitor. Test visibility remains an effective runtime policy
    /// rather than a forced persistent preference.
    /// </summary>
    public sealed class PerfOverlaySettings : IModSettings, IModSettingsActions
    {
        /// <summary>
        /// Saved default for ordinary game launches; test runs can show the overlay without rewriting this value.
        /// </summary>
        public static bool Enabled { get; private set; }

        /// <summary>
        /// Validated persisted corner identifier; unknown values fall back to top_right without changing viewport
        /// size.
        /// </summary>
        public static string Corner { get; private set; } = "top_right";

        /// <summary>
        /// Whether the next four-Hz refresh includes the longer diagnostic rows; numeric sampling remains
        /// unchanged.
        /// </summary>
        public static bool Detailed { get; private set; }

        /// <summary>
        /// Exposes enabled, corner, detail and copy-action controls through the existing mod settings UI.
        /// </summary>
        public IReadOnlyList<ModSettingDefinition> GetDefinitions()
        {
            return new List<ModSettingDefinition>
            {
                new()
                {
                    Key = "enabled",
                    Name = "perf_overlay/enabled",
                    Type = SettingType.Bool,
                    DefaultValue = "false"
                },
                new()
                {
                    Key = "corner",
                    Name = "perf_overlay/corner",
                    Type = SettingType.Enum,
                    DefaultValue = "top_right",
                    Options = new()
                    {
                        ["top_left"] = "perf_overlay/top_left",
                        ["top_right"] = "perf_overlay/top_right",
                        ["bottom_left"] = "perf_overlay/bottom_left",
                        ["bottom_right"] = "perf_overlay/bottom_right"
                    }
                },
                new()
                {
                    Key = "detailed",
                    Name = "perf_overlay/detailed",
                    Type = SettingType.Bool,
                    DefaultValue = "false"
                },
                new()
                {
                    Key = "copy",
                    Name = "perf_overlay/snapshot",
                    Type = SettingType.Action,
                    ActionText = "perf_overlay/copy"
                }
            };
        }

        /// <summary>
        /// Writes only this mod's three persistent presentation preferences into the supplied settings blob.
        /// </summary>
        public void WriteValues(DataBlob blob)
        {
            blob.SetBool("enabled", Enabled);
            blob.SetString("corner", Corner);
            blob.SetBool("detailed", Detailed);
        }

        /// <summary>
        /// Reads and validates preferences, then requests the current monitor to refresh. No display mode, renderer
        /// or game simulation setting is changed.
        /// </summary>
        public void ReadValues(DataBlob blob)
        {
            Enabled = blob.GetBool("enabled", false);
            var corner = blob.GetString("corner", "top_right");
            Corner = corner is "top_left" or "bottom_left" or "bottom_right" ? corner : "top_right";
            Detailed = blob.GetBool("detailed", false);
            PerfOverlayNode.Current?.ApplySettings();
        }

        /// <summary>
        /// Handles only the copy action and returns its localized clipboard outcome; unknown action IDs return
        /// null.
        /// </summary>
        public string RunAction(string key)
        {
            if (key != "copy")
            {
                return null;
            }

            PerfOverlayNode.Current?.CopySnapshot();
            return TranslationServer.Translate(
                PerfOverlayNode.Current?.ClipboardCopied == true
                    ? "perf_overlay/copied"
                    : "perf_overlay/copy_busy");
        }
    }
}
