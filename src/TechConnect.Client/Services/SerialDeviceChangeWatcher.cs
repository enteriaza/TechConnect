// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    SerialDeviceChangeWatcher.cs
//
// Purpose:
// Registers for Windows COM device interface arrival/removal on an owner window.
//
// -----------------------------------------------------------------------------

using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace TechConnect.Client.Services;

/// <summary>
/// Registers for serial COM device interface arrival/removal notifications on an owner window.
/// The owner must forward <c>WM_DEVICECHANGE</c> messages via <see cref="ProcessMessage"/>.
/// </summary>
/// <remarks>
/// <para>
/// Uses <c>RegisterDeviceNotification</c> with <c>GUID_DEVINTERFACE_COMPORT</c>.
/// Raised events indicate Windows reported a COM interface arrival or removal —
/// not that TechConnect.Client opened/closed a port, and not that friendly-name discovery
/// has already refreshed.
/// </para>
/// <para>
/// Must be constructed and disposed on the UI thread that owns <see cref="Control"/>.
/// <see cref="ProcessMessage"/> is expected on that same message pump. Disposal
/// unregisters the notification and detaches handle lifetime handlers.
/// </para>
/// </remarks>
internal sealed class SerialDeviceChangeWatcher : IDisposable
{
    /// <summary>Windows <c>WM_DEVICECHANGE</c> message id.</summary>
    internal const int WmDeviceChange = 0x0219;

    /// <summary>DBT_DEVICEARRIVAL.</summary>
    private const int DbtDeviceArrival = 0x8000;

    /// <summary>DBT_DEVICEREMOVECOMPLETE.</summary>
    private const int DbtDeviceRemoveComplete = 0x8004;

    /// <summary>DBT_DEVTYP_DEVICEINTERFACE.</summary>
    private const int DbtDevTypDeviceInterface = 0x00000005;

    /// <summary>DEVICE_NOTIFY_WINDOW_HANDLE.</summary>
    private const int DeviceNotifyWindowHandle = 0x00000000;

    /// <summary>GUID_DEVINTERFACE_COMPORT — serial port device interface class.</summary>
    private static readonly Guid ComPortInterfaceGuid = new("86E0D1E0-8089-11D0-9CE4-08003E301F73");

    /// <summary>WinForms control whose HWND receives device notifications.</summary>
    private readonly Control _owner;

    /// <summary>Logger for registration failures and change notifications.</summary>
    private readonly ILogger _logger;

    /// <summary>Handle from <c>RegisterDeviceNotification</c>, or zero when unregistered.</summary>
    private IntPtr _notificationHandle = IntPtr.Zero;

    /// <summary>Set once dispose begins; suppresses further registration/processing.</summary>
    private bool _disposed;

    /// <summary>
    /// Begins watching for COM interface changes on <paramref name="owner"/>.
    /// </summary>
    /// <param name="owner">Control whose window handle receives notifications.</param>
    /// <param name="logger">Structured logger.</param>
    /// <remarks>
    /// Registers immediately when a handle already exists; otherwise waits for
    /// <see cref="Control.HandleCreated"/>. Failed registration is logged; manual
    /// refresh remains available to the UI.
    /// </remarks>
    public SerialDeviceChangeWatcher(Control owner, ILogger logger)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _owner.HandleCreated += OwnerOnHandleCreated;
        _owner.HandleDestroyed += OwnerOnHandleDestroyed;

        if (_owner.IsHandleCreated)
        {
            RegisterNotification(_owner.Handle);
        }
    }

    /// <summary>
    /// Raised when a COM device-interface arrival or removal is observed.
    /// </summary>
    /// <remarks>
    /// Does not include the specific port name. Subscribers typically re-run discovery.
    /// </remarks>
    public event EventHandler? SerialDevicesChanged;

    /// <summary>
    /// Filters a window message and raises <see cref="SerialDevicesChanged"/> for COM arrivals/removals.
    /// </summary>
    /// <param name="m">Message forwarded from the owner window procedure.</param>
    public void ProcessMessage(Message m)
    {
        if (_disposed || m.Msg != WmDeviceChange)
        {
            return;
        }

        var eventType = m.WParam.ToInt32();
        if (eventType is not (DbtDeviceArrival or DbtDeviceRemoveComplete))
        {
            return;
        }

        if (m.LParam == IntPtr.Zero)
        {
            return;
        }

        var header = Marshal.PtrToStructure<DevBroadcastHdr>(m.LParam);
        if (header.DeviceType != DbtDevTypDeviceInterface)
        {
            return;
        }

        var deviceInterface = Marshal.PtrToStructure<DevBroadcastDeviceInterface>(m.LParam);
        if (deviceInterface.ClassGuid != ComPortInterfaceGuid)
        {
            return;
        }

        var changeKind = eventType == DbtDeviceArrival ? "arrival" : "removal";
        _logger.LogInformation("Serial COM device {ChangeKind} notification received", changeKind);
        SerialDevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Registers notifications when the owner HWND is (re)created.
    /// </summary>
    private void OwnerOnHandleCreated(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        RegisterNotification(_owner.Handle);
    }

    /// <summary>
    /// Unregisters notifications when the owner HWND is destroyed.
    /// </summary>
    private void OwnerOnHandleDestroyed(object? sender, EventArgs e)
    {
        UnregisterNotification();
    }

    /// <summary>
    /// Calls <c>RegisterDeviceNotification</c> for the COM port interface GUID.
    /// </summary>
    /// <param name="hwnd">Owner window handle.</param>
    private void RegisterNotification(IntPtr hwnd)
    {
        if (_notificationHandle != IntPtr.Zero || hwnd == IntPtr.Zero)
        {
            return;
        }

        var filter = new DevBroadcastDeviceInterface
        {
            Size = Marshal.SizeOf<DevBroadcastDeviceInterface>(),
            DeviceType = DbtDevTypDeviceInterface,
            ClassGuid = ComPortInterfaceGuid
        };

        var filterPtr = Marshal.AllocHGlobal(filter.Size);
        try
        {
            Marshal.StructureToPtr(filter, filterPtr, fDeleteOld: false);
            _notificationHandle = RegisterDeviceNotification(hwnd, filterPtr, DeviceNotifyWindowHandle);
            if (_notificationHandle == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                _logger.LogWarning(
                    "Failed to register serial device-change notifications. Win32 error {ErrorCode}. Manual Refresh remains available.",
                    error);
            }
            else
            {
                _logger.LogInformation("Registered serial COM device-change notifications");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(filterPtr);
        }
    }

    /// <summary>
    /// Calls <c>UnregisterDeviceNotification</c> when a prior registration exists.
    /// </summary>
    private void UnregisterNotification()
    {
        if (_notificationHandle == IntPtr.Zero)
        {
            return;
        }

        if (!UnregisterDeviceNotification(_notificationHandle))
        {
            var error = Marshal.GetLastWin32Error();
            _logger.LogWarning(
                "Failed to unregister serial device-change notifications. Win32 error {ErrorCode}",
                error);
        }

        _notificationHandle = IntPtr.Zero;
    }

    /// <summary>
    /// Detaches owner events and unregisters any active device notification.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _owner.HandleCreated -= OwnerOnHandleCreated;
        _owner.HandleDestroyed -= OwnerOnHandleDestroyed;
        UnregisterNotification();
    }

    /// <summary>P/Invoke: register device interface notifications for a window.</summary>
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr RegisterDeviceNotification(IntPtr hRecipient, IntPtr notificationFilter, int flags);

    /// <summary>P/Invoke: unregister a prior device notification handle.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterDeviceNotification(IntPtr handle);

    /// <summary>Partial DEV_BROADCAST_HDR layout used to filter device type.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DevBroadcastHdr
    {
        /// <summary>Structure size in bytes.</summary>
        public int Size;

        /// <summary>Device broadcast type (expect device interface).</summary>
        public int DeviceType;

        /// <summary>Reserved field required by the Windows layout.</summary>
        public int Reserved;
    }

    /// <summary>Partial DEV_BROADCAST_DEVICEINTERFACE layout for class GUID filtering.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevBroadcastDeviceInterface
    {
        /// <summary>Structure size in bytes.</summary>
        public int Size;

        /// <summary>Device broadcast type.</summary>
        public int DeviceType;

        /// <summary>Reserved field required by the Windows layout.</summary>
        public int Reserved;

        /// <summary>Device interface class GUID.</summary>
        public Guid ClassGuid;

        /// <summary>
        /// First character of the variable-length device name (layout placeholder only;
        /// name string is not consumed by this watcher).
        /// </summary>
        public char Name;
    }
}
