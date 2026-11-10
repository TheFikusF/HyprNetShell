using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using HyprNetShell.Core.Logging;

namespace HyprNetShell.Core.Features.System;

internal sealed class ColorTemperatureBackend : IDisposable
{
    private static class Native
    {
        private const string LIBRARY = "hypr_layer";

        [DllImport(LIBRARY, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr hypr_temperature_create();

        [DllImport(LIBRARY, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void hypr_temperature_destroy(IntPtr handle);

        [DllImport(LIBRARY, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int hypr_temperature_poll(IntPtr handle);

        [DllImport(LIBRARY, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int hypr_temperature_set_temperature(IntPtr handle, int kelvin);

        [DllImport(LIBRARY, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int hypr_temperature_is_available(IntPtr handle);

        [DllImport(LIBRARY, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int hypr_temperature_is_active(IntPtr handle);

        [DllImport(LIBRARY, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr hypr_temperature_get_error(IntPtr handle);
    }

    private sealed record Request(int Kelvin, CancellationToken Cancellation, TaskCompletionSource<bool> Completion);
    private sealed record State(bool Available, bool Active, int Temperature);

    private const int IDENTITY_TEMPERATURE = 6500;
    private const int RETRY_SECONDS = 30;

    private readonly Lock _gate = new();
    private readonly BlockingCollection<Request> _requests = new();
    private readonly Task _worker;
    private State _state = new(false, false, IDENTITY_TEMPERATURE);
    private bool _disposed;
    private bool _stopped;

    internal bool Available => Volatile.Read(ref _state).Available;
    internal bool Active => Volatile.Read(ref _state).Active;
    internal int Temperature => Volatile.Read(ref _state).Temperature;

    internal ColorTemperatureBackend()
    {
        _worker = Task.Factory.StartNew(Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    internal Task<bool> SetAsync(int kelvin, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled<bool>(cancellationToken);
            }

            if (_disposed || _stopped)
            {
                return Task.FromResult(false);
            }

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _requests.Add(new Request(Math.Clamp(kelvin, 2000, IDENTITY_TEMPERATURE), cancellationToken, completion));
            return completion.Task.WaitAsync(cancellationToken);
        }
    }

    private void Run()
    {
        var handle = IntPtr.Zero;
        var desired = IDENTITY_TEMPERATURE;
        var nextAttempt = 0L;
        Request? pending = null;
        try
        {
            while (!Volatile.Read(ref _disposed))
            {
                if (handle == IntPtr.Zero && Environment.TickCount64 >= nextAttempt)
                {
                    handle = Native.hypr_temperature_create();
                    if (handle == IntPtr.Zero)
                    {
                        AppLogger.Warning("Display", $"Native screen temperature unavailable: {GetError(handle)}. Retrying in {RETRY_SECONDS}s; stop other color-control clients such as hyprsunset if they own the output transforms.");
                        nextAttempt = Environment.TickCount64 + RETRY_SECONDS * 1000;
                    }
                    else if (desired != IDENTITY_TEMPERATURE && Native.hypr_temperature_set_temperature(handle, desired) == 0)
                    {
                        AppLogger.Warning("Display", $"Could not restore screen temperature: {GetError(handle)}");
                    }
                }

                if (_requests.TryTake(out var request, 100))
                {
                    pending = request;
                    if (request.Cancellation.IsCancellationRequested)
                    {
                        request.Completion.TrySetCanceled(request.Cancellation);
                    }
                    else if (Volatile.Read(ref _disposed))
                    {
                        request.Completion.TrySetResult(false);
                    }
                    else
                    {
                        desired = request.Kelvin;
                        var success = handle != IntPtr.Zero && Native.hypr_temperature_set_temperature(handle, desired) != 0;
                        if (!success)
                        {
                            AppLogger.Warning("Display", $"Could not set screen temperature to {desired}K: {(handle == IntPtr.Zero ? "native color control is unavailable" : GetError(handle))}");
                        }

                        Publish(handle, success ? desired : Temperature);
                        request.Completion.TrySetResult(success);
                    }

                    pending = null;
                }

                if (handle != IntPtr.Zero)
                {
                    if (Native.hypr_temperature_poll(handle) == 0 || Native.hypr_temperature_is_available(handle) == 0)
                    {
                        AppLogger.Warning("Display", $"Screen temperature control lost: {GetError(handle)}. Retrying in {RETRY_SECONDS}s.");
                        Native.hypr_temperature_destroy(handle);
                        handle = IntPtr.Zero;
                        nextAttempt = Environment.TickCount64 + RETRY_SECONDS * 1000;
                    }

                    Publish(handle, desired);
                }
            }
        }

        catch (Exception exception)
        {
            AppLogger.Error("Display", "Native screen temperature worker stopped", exception);
        }

        finally
        {
            pending?.Completion.TrySetResult(false);
            lock (_gate)
            {
                _stopped = true;
                _requests.CompleteAdding();
            }

            while (_requests.TryTake(out var request))
            {
                request.Completion.TrySetResult(false);
            }

            try
            {
                if (handle != IntPtr.Zero)
                {
                    Native.hypr_temperature_destroy(handle);
                }
            }
            catch (Exception exception)
            {
                AppLogger.Warning("Display", "Could not release native screen temperature control", exception);
            }
            finally
            {
                Volatile.Write(ref _state, new State(false, false, IDENTITY_TEMPERATURE));
                _requests.Dispose();
            }
        }
    }

    private void Publish(IntPtr handle, int temperature)
    {
        var available = handle != IntPtr.Zero && Native.hypr_temperature_is_available(handle) != 0;
        var active = available && Native.hypr_temperature_is_active(handle) != 0;
        Volatile.Write(ref _state, new State(available, active, available ? temperature : IDENTITY_TEMPERATURE));
    }

    private static string GetError(IntPtr handle) =>
        Marshal.PtrToStringUTF8(Native.hypr_temperature_get_error(handle)) ?? "Unknown CTM control error";

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            Volatile.Write(ref _disposed, true);
            if (!_stopped)
            {
                _requests.CompleteAdding();
            }
        }

        if (!_worker.Wait(TimeSpan.FromSeconds(3)))
        {
            AppLogger.Warning("Display", "Screen temperature worker is still releasing its Wayland connection");
        }
    }
}
