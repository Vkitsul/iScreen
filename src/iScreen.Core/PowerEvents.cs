using System.Runtime.InteropServices;

namespace IScreen.Host;

/// <summary>
/// Suspend/resume callbacks straight from the power manager (PowerRegisterSuspendResumeNotification).
/// Unlike WM_POWERBROADCAST this doesn't depend on a window's message loop being responsive.
/// </summary>
public sealed class PowerEvents : IDisposable
{
    const uint DEVICE_NOTIFY_CALLBACK = 2;
    const int PBT_APMSUSPEND = 4, PBT_APMRESUMESUSPEND = 7, PBT_APMRESUMEAUTOMATIC = 18;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate uint Callback(IntPtr context, int type, IntPtr setting);

    [StructLayout(LayoutKind.Sequential)]
    struct DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS
    {
        public IntPtr Callback;
        public IntPtr Context;
    }

    [DllImport("powrprof.dll")]
    static extern uint PowerRegisterSuspendResumeNotification(uint flags, IntPtr recipient, out IntPtr handle);

    [DllImport("powrprof.dll")]
    static extern uint PowerUnregisterSuspendResumeNotification(IntPtr handle);

    readonly Callback _callback; // must stay alive while registered
    readonly IntPtr _parameters;
    IntPtr _handle;

    /// <summary>Raised on a system thread just before sleep; Windows waits briefly for it.</summary>
    public event Action? Suspending;
    public event Action? Resumed;

    public PowerEvents()
    {
        _callback = OnPowerEvent;
        _parameters = Marshal.AllocHGlobal(Marshal.SizeOf<DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS>());
        Marshal.StructureToPtr(new DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS
        {
            Callback = Marshal.GetFunctionPointerForDelegate(_callback),
        }, _parameters, false);
        uint r = PowerRegisterSuspendResumeNotification(DEVICE_NOTIFY_CALLBACK, _parameters, out _handle);
        if (r != 0) Console.WriteLine($"power notifications unavailable ({r})");
    }

    uint OnPowerEvent(IntPtr context, int type, IntPtr setting)
    {
        try
        {
            switch (type)
            {
                case PBT_APMSUSPEND: Suspending?.Invoke(); break;
                case PBT_APMRESUMESUSPEND or PBT_APMRESUMEAUTOMATIC: Resumed?.Invoke(); break;
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"power event handler failed: {e.Message}");
        }
        return 0;
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero) PowerUnregisterSuspendResumeNotification(_handle);
        _handle = IntPtr.Zero;
        Marshal.FreeHGlobal(_parameters);
    }
}
