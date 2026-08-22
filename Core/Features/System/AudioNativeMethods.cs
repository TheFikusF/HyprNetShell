using System.Runtime.InteropServices;

namespace HyprNetShell.Core.Features.System;

internal static class AudioNativeMethods
{
    internal const uint AbiVersion = 1;
    private const string LibraryName = "hypr_audio";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void SnapshotCallback(IntPtr userData, IntPtr snapshot);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Device
    {
        internal uint StructSize;
        internal uint Id;
        internal IntPtr Name;
        internal int Volume;
        internal byte Muted;
        internal byte Active;
        internal ushort Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Snapshot
    {
        internal uint StructSize;
        internal uint AbiVersion;
        internal IntPtr Outputs;
        internal uint OutputCount;
        internal IntPtr Inputs;
        internal uint InputCount;
        internal byte IsRecording;
        internal byte Reserved0;
        internal byte Reserved1;
        internal byte Reserved2;
        internal byte Reserved3;
        internal byte Reserved4;
        internal byte Reserved5;
        internal byte Reserved6;
    }

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr hypr_audio_create(IntPtr callback, IntPtr userData);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void hypr_audio_destroy(IntPtr audio);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint hypr_audio_get_abi_version();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int hypr_audio_is_available(IntPtr audio);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int hypr_audio_set_default(IntPtr audio, uint deviceId);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int hypr_audio_set_volume(IntPtr audio, uint deviceId, int volumePercent);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int hypr_audio_set_muted(IntPtr audio, uint deviceId, int muted);
}
