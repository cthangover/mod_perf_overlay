using System;
using Godot;

namespace Cthangover.Mods.PerformanceOverlay
{
    /// <summary>
    /// The graph redraws with text at 4 Hz; its buffer is reused and never scales or samples game time.
    /// </summary>
    public partial class PerfFrameGraph : Control
    {
        private readonly double[] samples = new double[200];
        private int count;

        /// <summary>
        /// Copies the newest visible history into this graph's fixed buffer and requests a redraw. The caller
        /// retains the frame window; redraw cadence follows the four-Hz text refresh.
        /// </summary>
        public void Refresh(PerfFrameWindow frames)
        {
            count = frames.Graph(samples);
            QueueRedraw();
        }

        /// <summary>
        /// Renders the fixed history in viewport-local coordinates, clipping the scale at 100 ms and highlighting
        /// intervals above 50 ms. Drawing never advances the measurement window.
        /// </summary>
        public override void _Draw()
        {
            DrawRect(new Rect2(Vector2.Zero, Size), new Color(.05f, .07f, .09f, .85f));
            var limit = 100f;
            DrawLine(new Vector2(0, Size.Y * .5f), new Vector2(Size.X, Size.Y * .5f), new Color(.6f, .3f, .1f));
            for (var i = 1; i < count; i++)
            {
                DrawLine(
                    new Vector2(
                        (i - 1) * Size.X / Math.Max(1, count - 1),
                        Size.Y * (1 - Math.Min(limit, (float)samples[i - 1]) / limit)),
                    new Vector2(i * Size.X / Math.Max(1, count - 1), Size.Y * (1 - Math.Min(limit, (float)samples[i]) / limit)),
                    samples[i] > 50 ? Colors.OrangeRed : Colors.LightGreen,
                    1
                );
            }
        }
    }
}
