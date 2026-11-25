using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace HyprNetShell.Authorization;

internal static partial class AuthorizationNative
{
    private const string LIBRARY = "hypr_layer";

    [LibraryImport(LIBRARY, EntryPoint = "hypr_polkit_start")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint StartPolkit();

    [LibraryImport(LIBRARY, EntryPoint = "hypr_polkit_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void DestroyPolkit(nint handle);

    [LibraryImport(LIBRARY, EntryPoint = "hypr_polkit_prompt")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial ulong PollPolkit(nint handle, ref byte message, nuint capacity);

    [LibraryImport(LIBRARY, EntryPoint = "hypr_polkit_answer")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void AnswerPolkit(nint handle, ulong generation, ref byte response);

    [LibraryImport(LIBRARY, EntryPoint = "hypr_su_start", StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nint StartSu(string command);

    [LibraryImport(LIBRARY, EntryPoint = "hypr_su_poll")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int PollSu(nint handle);

    [LibraryImport(LIBRARY, EntryPoint = "hypr_su_answer")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int AnswerSu(nint handle, ref byte response);

    [LibraryImport(LIBRARY, EntryPoint = "hypr_su_destroy")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void DestroySu(nint handle);
}
