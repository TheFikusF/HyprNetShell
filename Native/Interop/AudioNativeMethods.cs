using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace HyprNetShell.Native.Interop;

internal static partial class AudioNativeMethods
{

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

    internal const uint ABI_VERSION = 1;
    private const string LIBRARY_NAME = "hypr_audio";

    [LibraryImport(LIBRARY_NAME)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial IntPtr hypr_audio_create(IntPtr callback, IntPtr userData);

    [LibraryImport(LIBRARY_NAME)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void hypr_audio_destroy(IntPtr audio);

    [LibraryImport(LIBRARY_NAME)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint hypr_audio_get_abi_version();

    [LibraryImport(LIBRARY_NAME)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int hypr_audio_is_available(IntPtr audio);

    [LibraryImport(LIBRARY_NAME)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int hypr_audio_set_default(IntPtr audio, uint deviceId);

    [LibraryImport(LIBRARY_NAME)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int hypr_audio_set_volume(IntPtr audio, uint deviceId, int volumePercent);

    [LibraryImport(LIBRARY_NAME)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int hypr_audio_set_muted(IntPtr audio, uint deviceId, int muted);
}
