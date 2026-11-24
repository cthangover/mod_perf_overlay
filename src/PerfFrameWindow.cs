using System;

namespace Cthangover.Mods.PerformanceOverlay
{
    /// <summary>
    /// Exact nearest-rank statistics over five wall-clock seconds. The fixed ring/sort scratch avoid per-frame
    /// allocation; refresh sorts only on demand.
    /// </summary>
    public sealed class PerfFrameWindow
    {
        private readonly double[] times;
        private readonly double[] values;
        private readonly double[] scratch;
        private int head;
        private int count;
        private double lastTime = -1;
        private long over50;
        private long over250;
        private long dropped;
        private double? lastHitch;

        /// <summary>
        /// Retained sample count after the latest Add or Summarize trim; merely reading it does not advance time.
        /// </summary>
        public int Count
        {
            get
            {
                return count;
            }
        }

        /// <summary>
        /// Allocates fixed ring and sort buffers once. Capacity is bounded to 2 through 262144 samples;
        /// subsequent frame insertion reuses these arrays.
        /// </summary>
        public PerfFrameWindow(int capacity = 32768)
        {
            if (capacity < 2 || capacity > 262144)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity));
            }

            times = new double[capacity];
            values = new double[capacity];
            scratch = new double[capacity];
        }

        /// <summary>
        /// Accepts finite nonnegative intervals at monotonic session times, expires the old window and records
        /// cumulative hitches. Capacity overflow evicts the oldest sample and increments the drop counter.
        /// </summary>
        public void Add(double seconds, double milliseconds)
        {
            if (!double.IsFinite(seconds) || seconds < lastTime || seconds < 0 || !double.IsFinite(milliseconds)
                || milliseconds < 0)
            {
                throw new ArgumentException("Frame samples must be finite, nonnegative and ordered");
            }

            lastTime = seconds;
            Trim(seconds);
            if (count == values.Length)
            {
                head = (head + 1) % values.Length;
                count--;
                dropped++;
            }

            var index = (head + count) % values.Length;
            times[index] = seconds;
            values[index] = milliseconds;
            count++;
            if (milliseconds > 50)
            {
                over50++;
                lastHitch = seconds;
            }

            if (milliseconds > 250)
            {
                over250++;
            }
        }

        /// <summary>
        /// Starts an independent window, discarding frame and hitch history while retaining the allocated buffers.
        /// </summary>
        public void Reset()
        {
            head = count = 0;
            lastTime = -1;
            over50 = over250 = dropped = 0;
            lastHitch = null;
        }

        private void Trim(double seconds)
        {
            while (count > 0 && times[head] < seconds - 5)
            {
                head = (head + 1) % values.Length;
                count--;
            }
        }

        /// <summary>
        /// Trims to the requested monotonic time and fills the caller-owned summary using reusable sort scratch.
        /// Empty windows zero frame statistics while retaining session hitch history.
        /// </summary>
        public void Summarize(double seconds, PerfFrameSummary result)
        {
            if (!double.IsFinite(seconds) || seconds < lastTime)
            {
                throw new ArgumentException("Window time cannot run backwards");
            }

            Trim(seconds);
            result.Count = count;
            result.Over50 = over50;
            result.Over250 = over250;
            result.LastHitchSeconds = lastHitch;
            result.DroppedSamples = dropped;
            result.CurrentMs = result.MeanMs = result.P95Ms = result.P99Ms = result.MaxMs = 0;
            if (count == 0)
            {
                return;
            }

            double sum = 0;
            for (var i = 0; i < count; i++)
            {
                scratch[i] = values[(head + i) % values.Length];
                sum += scratch[i];
            }

            Array.Sort(scratch, 0, count);
            result.CurrentMs = values[(head + count - 1) % values.Length];
            result.MeanMs = sum / count;
            result.P95Ms = scratch[(int)Math.Ceiling(count * .95) - 1];
            result.P99Ms = scratch[(int)Math.Ceiling(count * .99) - 1];
            result.MaxMs = scratch[count - 1];
        }

        /// <summary>
        /// Caller-owned graph buffer is filled oldest to newest; zero unused entries are outside the returned
        /// count.
        /// </summary>
        public int Graph(double[] destination)
        {
            var n = Math.Min(count, destination.Length);
            for (var i = 0; i < n; i++)
            {
                destination[i] = values[(head + count - n + i) % values.Length];
            }

            return n;
        }
    }
}
