using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace Cthangover.Mods.PerformanceOverlay
{
    /// <summary>
    /// Window capture handshake over a PID-scoped local pipe. Only the main thread touches visibility; ACK follows
    /// a real clean draw. No window/display settings or gameplay input change.
    /// </summary>
    public sealed class PerfCaptureBridge
    {
        private readonly PerfOverlayNode owner;
        private readonly CancellationTokenSource stop = new();
        private readonly Task work;
        private NamedPipeServerStream pipe;
        private TaskCompletionSource<bool> drawn;
        private int requested;
        private int applied;
        private bool closed;
        /// <summary>
        /// Starts one owned pipe task and post-draw subscription after the monitor enters the engine. The owner
        /// joins Stop before native teardown; workers never touch controls directly.
        /// </summary>
        public PerfCaptureBridge(PerfOverlayNode owner)
        {
            this.owner = owner;
            RenderingServer.FramePostDraw += PostDraw;
            work = Run();
        }

        private async Task Run()
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using var stream = new NamedPipeServerStream(
                        "cthangover-perf-overlay-" + System.Environment.ProcessId,
                        PipeDirection.InOut,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous
                    );
                    pipe = stream;
                    await stream.WaitForConnectionAsync(stop.Token).ConfigureAwait(false);
                    var command = new byte[1];
                    if (await stream.ReadAsync(command, stop.Token).ConfigureAwait(false) != 1 || command[0] != 1)
                    {
                        continue;
                    }

                    var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    Volatile.Write(ref drawn, completion);
                    Volatile.Write(ref requested, 1);
                    await completion.Task.WaitAsync(stop.Token).ConfigureAwait(false);
                    command[0] = 1;
                    await stream.WriteAsync(command, stop.Token).ConfigureAwait(false);
                    await stream.FlushAsync(stop.Token).ConfigureAwait(false);
                    await stream.ReadAsync(command, stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (IOException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
                finally
                {
                    Volatile.Write(ref requested, 0);
                    pipe = null;
                }
            }
        }

        /// <summary>
        /// Applies the worker request to monitor visibility on the main thread. Called even while diagnostics are
        /// disabled so a window-capture handshake can complete without touching gameplay input.
        /// </summary>
        public void Pump()
        {
            var next = Volatile.Read(ref requested);
            if (next == applied)
            {
                return;
            }

            applied = next;
            owner.SetCaptureHidden(next != 0);
        }

        private void PostDraw()
        {
            if (applied == 1 && Volatile.Read(ref requested) == 1)
            {
                Volatile.Read(ref drawn)?.TrySetResult(true);
            }
        }

        /// <summary>
        /// Cancels and closes the bridge, then joins its asynchronous pipe worker while the engine is alive.
        /// ApplicationExit uses this barrier before native teardown.
        /// </summary>
        public async Task Stop()
        {
            Close();
            try
            {
                await work;
            }
            catch (OperationCanceledException)
            {
            }
        }

        /// <summary>
        /// Synchronously detaches post-draw, cancels pending I/O and disposes the current pipe. Repeated calls are
        /// harmless; Stop provides the separate asynchronous join.
        /// </summary>
        public void Close()
        {
            if (closed)
            {
                return;
            }

            closed = true;
            RenderingServer.FramePostDraw -= PostDraw;
            stop.Cancel();
            pipe?.Dispose();
        }
    }
}
