using System.Runtime.InteropServices;
using System.Text;

namespace AntLslStreamer;

/// <summary>
/// Minimal P/Invoke bindings for the liblsl C API (liblsl >= 1.14).
/// Only the functions needed by this application are declared.
/// </summary>
internal static class Lsl
{
    private const string Dll = "lsl";

    public const double IrregularRate = 0.0;
    public const double Forever = 32000000.0;

    public enum ChannelFormat
    {
        Float32 = 1,
        Double64 = 2,
        String = 3,
        Int32 = 4,
    }

    [Flags]
    public enum ProcFlags : uint
    {
        None = 0,
        ClockSync = 1,
        Dejitter = 2,
        Monotonize = 4,
        ThreadSafe = 8,
        All = 1 | 2 | 4 | 8,
    }

    // ---- library / clock -------------------------------------------------------------------
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int lsl_library_version();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern double lsl_local_clock();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void lsl_destroy_string(IntPtr s);

    // ---- stream info / XML description --------------------------------------------------------
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr lsl_create_streaminfo(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string type,
        int channelCount, double nominalSrate, ChannelFormat format,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string sourceId);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void lsl_destroy_streaminfo(IntPtr info);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr lsl_get_desc(IntPtr info);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr lsl_append_child(IntPtr e, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr lsl_append_child_value(IntPtr e,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr lsl_get_name(IntPtr info);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr lsl_get_type(IntPtr info);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr lsl_get_source_id(IntPtr info);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int lsl_get_channel_count(IntPtr info);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern double lsl_get_nominal_srate(IntPtr info);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern ChannelFormat lsl_get_channel_format(IntPtr info);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr lsl_get_xml(IntPtr info);

    // ---- outlet -------------------------------------------------------------------------------
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr lsl_create_outlet(IntPtr info, int chunkSize, int maxBuffered);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void lsl_destroy_outlet(IntPtr outlet);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int lsl_have_consumers(IntPtr outlet);

    // NB: 'unsigned long' is 32 bit on Windows.
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int lsl_push_chunk_ftp(IntPtr outlet, float[] data, uint dataElements, double timestamp, int pushthrough);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int lsl_push_sample_strtp(IntPtr outlet, IntPtr[] data, double timestamp, int pushthrough);

    // ---- resolve / inlet (used by the built-in test inlet) ------------------------------------
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int lsl_resolve_byprop(IntPtr[] buffer, uint bufferElements,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string prop,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int minimum, double timeout);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr lsl_create_inlet(IntPtr info, int maxBuflen, int maxChunklen, int recover);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void lsl_destroy_inlet(IntPtr inlet);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr lsl_get_fullinfo(IntPtr inlet, double timeout, out int ec);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern void lsl_open_stream(IntPtr inlet, double timeout, out int ec);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int lsl_set_postprocessing(IntPtr inlet, ProcFlags flags);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern double lsl_time_correction(IntPtr inlet, double timeout, out int ec);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint lsl_pull_chunk_f(IntPtr inlet, float[] dataBuffer, double[] timestampBuffer,
        uint dataBufferElements, uint timestampBufferElements, double timeout, out int ec);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern double lsl_pull_sample_str(IntPtr inlet, IntPtr[] buffer, int bufferElements, double timeout, out int ec);

    // ---- helpers ------------------------------------------------------------------------------
    public static string PtrToString(IntPtr p) => p == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(p) ?? "";

    /// <summary>Converts a string returned by liblsl that must be freed with lsl_destroy_string.</summary>
    public static string TakeString(IntPtr p)
    {
        if (p == IntPtr.Zero) return "";
        try { return Marshal.PtrToStringUTF8(p) ?? ""; }
        finally { lsl_destroy_string(p); }
    }

    public static IntPtr StringToUtf8(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s + "\0");
        var p = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, p, bytes.Length);
        return p;
    }
}
