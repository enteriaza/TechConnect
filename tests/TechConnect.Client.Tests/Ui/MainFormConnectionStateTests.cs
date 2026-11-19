// -----------------------------------------------------------------------------
// Project: TechConnect.Client.Tests
// File:    MainFormConnectionStateTests.cs
//
// Purpose:
// Verifies MainForm connection-state UI updates driven by remote session snapshots.
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using TechConnect.Client.Forms;
using TechConnect.Client.Models;
using TechConnect.Client.Tests.Support;

namespace TechConnect.Client.Tests.Ui;

/// <summary>
/// STA tests for MainForm connect/disconnect UI vs serial/remote session states.
/// </summary>
public sealed class MainFormConnectionStateTests
{
    /// <summary>
    /// Creates a MainForm with fake serial/remote dependencies and seeded ports.
    /// </summary>
    private static (MainForm Form, FakeSerialConnectionService Serial, FakeRemoteSessionService Remote)
        CreateForm(params string[] ports)
    {
        var discovery = new FakeSerialPortDiscoveryService();
        discovery.SetPorts(ports.Length == 0 ? ["COM4"] : ports);
        var serial = new FakeSerialConnectionService();
        var remote = new FakeRemoteSessionService();
        var form = new MainForm(
            new CollectingLogger<MainForm>(),
            discovery,
            serial,
            remote);
        return (form, serial, remote);
    }

    /// <summary>
    /// Reads the private connect button via reflection.
    /// </summary>
    private static Button ConnectButton(MainForm form) =>
        UiReflection.GetPrivateField<Button>(form, "_connectButton");

    /// <summary>
    /// Reads the private serial status label via reflection.
    /// </summary>
    private static Label SerialStatus(MainForm form) =>
        UiReflection.GetPrivateField<Label>(form, "_serialStatusValueLabel");

    /// <summary>
    /// Reads the private remote status label via reflection.
    /// </summary>
    private static Label RemoteStatus(MainForm form) =>
        UiReflection.GetPrivateField<Label>(form, "_remoteStatusValueLabel");

    /// <summary>
    /// Reads the private error label via reflection.
    /// </summary>
    private static Label ErrorLabel(MainForm form) =>
        UiReflection.GetPrivateField<Label>(form, "_errorLabel");

    /// <summary>
    /// Reads the private VT terminal control via reflection.
    /// </summary>
    private static SerialTerminalControl ConsoleTerminal(MainForm form) =>
        UiReflection.GetPrivateField<SerialTerminalControl>(form, "_consoleTerminal");

    /// <summary>
    /// Polls a UI condition on the STA thread until true or timeout.
    /// </summary>
    private static async Task WaitForUiAsync(Func<bool> condition, string because, TimeSpan? timeout = null)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(5);
        var start = DateTime.UtcNow;
        while (!condition())
        {
            Application.DoEvents();
            if (DateTime.UtcNow - start > limit)
            {
                throw new TimeoutException(because);
            }

            await Task.Delay(15).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Clicks the Connect/Disconnect button and yields for UI processing.
    /// </summary>
    private static async Task ClickConnectAsync(MainForm form)
    {
        var click = typeof(MainForm).GetMethod(
            "ConnectButton_Click",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ConnectButton_Click not found.");
        click.Invoke(form, [ConnectButton(form), EventArgs.Empty]);
        await Task.Yield();
        Application.DoEvents();
    }

    /// <summary>
    /// Forces console buffer drain into the VT terminal for assertions.
    /// </summary>
    private static void FlushConsole(MainForm form)
    {
        var tick = typeof(MainForm).GetMethod(
            "ConsoleFlushTimer_Tick",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ConsoleFlushTimer_Tick not found.");
        tick.Invoke(form, [form, EventArgs.Empty]);
    }

    /// <summary>
    /// Scenario: connect with remote start held.
    /// Expected: serial opens immediately; button shows Disconnect before remote finishes.
    /// </summary>
    [Fact]
    public async Task Connect_OpensSerialImmediately_ShowsDisconnect_BeforeRemote()
    {
        await StaTask.Run(async () =>
        {
            var (form, serial, remote) = CreateForm("COM7");
            remote.HoldNextStart();
            using (form)
            {
                form.Show();
                await WaitForUiAsync(() => ConnectButton(form).Enabled, "ports loaded");

                try
                {
                    await ClickConnectAsync(form);
                    await WaitForUiAsync(
                        () => serial.IsOpen &&
                              ConnectButton(form).Text == "Disconnect" &&
                              SerialStatus(form).Text == "Connected",
                        "serial must be Connected with Disconnect before remote finishes");

                    // Remote start still held — serial already usable.
                    remote.StartCount.Should().BeLessThanOrEqualTo(1);
                    serial.OpenCallCount.Should().Be(1);
                }
                finally
                {
                    remote.ReleaseHeldStart();
                }
            }
        });
    }

    /// <summary>
    /// Scenario: remote not configured.
    /// Expected: serial stays open; not-configured message; Disconnect available.
    /// </summary>
    [Fact]
    public async Task MissingRemoteHost_KeepsSerialOpen_ShowsNotConfigured_Disconnect()
    {
        await StaTask.Run(async () =>
        {
            var (form, serial, remote) = CreateForm("COM4");
            remote.StartAsConfigurationError = true;
            using (form)
            {
                form.Show();
                await WaitForUiAsync(() => ConnectButton(form).Enabled, "ports loaded");

                await ClickConnectAsync(form);
                await WaitForUiAsync(
                    () => serial.IsOpen &&
                          ConnectButton(form).Text == "Disconnect" &&
                          RemoteStatus(form).Text == "Not configured",
                    "config error must not close serial");

                serial.IsOpen.Should().BeTrue();
                SerialStatus(form).Text.Should().Be("Connected");
                ConnectButton(form).Text.Should().Be("Disconnect");
                ErrorLabel(form).Text.Should().Contain("not configured");
            }
        });
    }

    /// <summary>
    /// Scenario: remote start fails transport.
    /// Expected: serial open; button remains Disconnect.
    /// </summary>
    [Fact]
    public async Task RemoteTransportFailure_KeepsSerialOpen_ButtonRemainsDisconnect()
    {
        await StaTask.Run(async () =>
        {
            var (form, serial, remote) = CreateForm("COM4");
            remote.StartAsRemoteFailure = true;
            using (form)
            {
                form.Show();
                await WaitForUiAsync(() => ConnectButton(form).Enabled, "ports loaded");

                await ClickConnectAsync(form);
                await WaitForUiAsync(
                    () => serial.IsOpen && ConnectButton(form).Text == "Disconnect",
                    "remote failure must leave serial connected");

                serial.IsOpen.Should().BeTrue();
                SerialStatus(form).Text.Should().Be("Connected");
            }
        });
    }

    /// <summary>
    /// Scenario: RX arrives while remote unconfigured.
    /// Expected: console shows RX text.
    /// </summary>
    [Fact]
    public async Task ConsoleShowsRx_WhileRemoteNotConfigured()
    {
        await StaTask.Run(async () =>
        {
            var (form, serial, remote) = CreateForm("COM4");
            remote.StartAsConfigurationError = true;
            using (form)
            {
                form.Show();
                await WaitForUiAsync(() => ConnectButton(form).Enabled, "ports loaded");
                await ClickConnectAsync(form);
                await WaitForUiAsync(() => serial.IsOpen, "serial open");

                serial.InjectRx("device>"u8.ToArray());
                FlushConsole(form);

                await WaitForUiAsync(
                    () => ConsoleTerminal(form).GetVisibleText().Contains("device>", StringComparison.Ordinal),
                    "local console must show RX while remote is not configured");
            }
        });
    }

    /// <summary>
    /// Scenario: RX while remote start held.
    /// Expected: console shows RX during connecting.
    /// </summary>
    [Fact]
    public async Task ConsoleShowsRx_WhileRemoteHeldConnecting()
    {
        await StaTask.Run(async () =>
        {
            var (form, serial, remote) = CreateForm("COM4");
            remote.HoldNextStart();
            using (form)
            {
                form.Show();
                await WaitForUiAsync(() => ConnectButton(form).Enabled, "ports loaded");

                try
                {
                    await ClickConnectAsync(form);
                    await WaitForUiAsync(() => serial.IsOpen, "serial open");

                    serial.InjectRx("boot\r\n"u8.ToArray());
                    FlushConsole(form);

                    await WaitForUiAsync(
                        () => ConsoleTerminal(form).GetVisibleText().Contains("boot", StringComparison.Ordinal),
                        "local console must show RX while remote is still connecting");
                }
                finally
                {
                    remote.ReleaseHeldStart();
                }
            }
        });
    }

    /// <summary>
    /// Scenario: remote fault after session started.
    /// Expected: serial stays open; Disconnect unchanged.
    /// </summary>
    [Fact]
    public async Task RemoteFailureAfterSession_DoesNotCloseSerial_OrChangeButton()
    {
        await StaTask.Run(async () =>
        {
            var (form, serial, remote) = CreateForm("COM8");
            using (form)
            {
                form.Show();
                await WaitForUiAsync(() => ConnectButton(form).Enabled, "ports loaded");
                await ClickConnectAsync(form);
                await WaitForUiAsync(() => serial.IsOpen && ConnectButton(form).Text == "Disconnect", "connected");

                remote.RaiseSnapshot(new RemoteSessionSnapshot
                {
                    SerialStatus = SerialLinkStatus.Connected,
                    RemoteStatus = RemoteSessionState.Error,
                    NocStatus = NocAttachmentState.Waiting,
                    ClientSessionId = null,
                    ServerSessionId = null,
                    Delivery = new RemoteDeliveryStats(),
                    LastRemoteError = "Remote connection lost."
                });

                await WaitForUiAsync(
                    () => RemoteStatus(form).Text == "Not configured",
                    "remote error reflected in remote status");

                serial.IsOpen.Should().BeTrue();
                ConnectButton(form).Text.Should().Be("Disconnect");
                SerialStatus(form).Text.Should().Be("Connected");
            }
        });
    }

    /// <summary>
    /// Scenario: disconnect while remote still connecting.
    /// Expected: serial closes.
    /// </summary>
    [Fact]
    public async Task Disconnect_ClosesSerial_WhileRemoteConnecting()
    {
        await StaTask.Run(async () =>
        {
            var (form, serial, remote) = CreateForm("COM4");
            remote.HoldNextStart();
            using (form)
            {
                form.Show();
                await WaitForUiAsync(() => ConnectButton(form).Enabled, "ports loaded");

                try
                {
                    await ClickConnectAsync(form);
                    await WaitForUiAsync(() => serial.IsOpen && ConnectButton(form).Text == "Disconnect", "serial up");

                    await ClickConnectAsync(form);
                    await WaitForUiAsync(
                        () => !serial.IsOpen && ConnectButton(form).Text == "Connect",
                        "disconnect must close serial even while remote start is held");

                    remote.StopCount.Should().BeGreaterThanOrEqualTo(1);
                }
                finally
                {
                    remote.ReleaseHeldStart();
                }
            }
        });
    }

    /// <summary>
    /// Scenario: disconnect while remote connected.
    /// Expected: session stopped and serial closed.
    /// </summary>
    [Fact]
    public async Task Disconnect_WorksWhileRemoteConnected()
    {
        await StaTask.Run(async () =>
        {
            var (form, serial, remote) = CreateForm("COM4");
            using (form)
            {
                form.Show();
                await WaitForUiAsync(() => ConnectButton(form).Enabled, "ports loaded");
                await ClickConnectAsync(form);
                await WaitForUiAsync(
                    () => serial.IsOpen && remote.State == RemoteSessionState.ConnectedWaitingForNoc,
                    "serial + remote up");

                await ClickConnectAsync(form);
                await WaitForUiAsync(
                    () => !serial.IsOpen && ConnectButton(form).Text == "Connect",
                    "disconnect closes serial");

                remote.State.Should().Be(RemoteSessionState.Disconnected);
            }
        });
    }

    /// <summary>
    /// Scenario: repeated connect clicks.
    /// Expected: serial open count does not duplicate.
    /// </summary>
    [Fact]
    public async Task RepeatedConnectClicks_DoNotOpenDuplicateSerial()
    {
        await StaTask.Run(async () =>
        {
            var (form, serial, remote) = CreateForm("COM4");
            serial.HoldNextOpen();
            using (form)
            {
                form.Show();
                await WaitForUiAsync(() => ConnectButton(form).Enabled, "ports loaded");

                try
                {
                    await ClickConnectAsync(form);
                    await WaitForUiAsync(() => serial.OpenCallCount >= 1, "first open started");

                    await ClickConnectAsync(form);
                    serial.OpenCallCount.Should().Be(1);

                    serial.ReleaseHeldOpen();
                    await WaitForUiAsync(
                        () => ConnectButton(form).Text == "Connect" || serial.IsOpen,
                        "connect or cancel settles");
                }
                finally
                {
                    serial.ReleaseHeldOpen();
                }
            }
        });
    }

    /// <summary>
    /// Scenario: repeated disconnect clicks.
    /// Expected: idempotent stop/close.
    /// </summary>
    [Fact]
    public async Task RepeatedDisconnectClicks_AreIdempotent()
    {
        await StaTask.Run(async () =>
        {
            var (form, serial, remote) = CreateForm("COM4");
            remote.HoldNextStop();
            using (form)
            {
                form.Show();
                await WaitForUiAsync(() => ConnectButton(form).Enabled, "ports loaded");
                await ClickConnectAsync(form);
                await WaitForUiAsync(() => serial.IsOpen && ConnectButton(form).Text == "Disconnect", "connected");

                try
                {
                    await ClickConnectAsync(form);
                    await WaitForUiAsync(
                        () => ConnectButton(form).Text == "Disconnecting..." || !serial.IsOpen,
                        "disconnect started (serial closes before remote stop finishes)");

                    await ClickConnectAsync(form);
                    await ClickConnectAsync(form);

                    remote.ReleaseHeldStop();
                    await WaitForUiAsync(
                        () => ConnectButton(form).Text == "Connect" && !serial.IsOpen,
                        "disconnect completes");

                    // Serial closed once; remote stop may have been requested once.
                    serial.IsOpen.Should().BeFalse();
                }
                finally
                {
                    remote.ReleaseHeldStop();
                }
            }
        });
    }

    /// <summary>
    /// Scenario: successful remote start.
    /// Expected: remote status connected; serial stays connected.
    /// </summary>
    [Fact]
    public async Task SuccessfulRemote_ShowsConnectedRemote_SerialStaysConnected()
    {
        await StaTask.Run(async () =>
        {
            var (form, serial, remote) = CreateForm("COM7");
            using (form)
            {
                form.Show();
                await WaitForUiAsync(() => ConnectButton(form).Enabled, "ports loaded");
                await ClickConnectAsync(form);
                await WaitForUiAsync(
                    () => serial.IsOpen &&
                          RemoteStatus(form).Text == "Connected" &&
                          ConnectButton(form).Text == "Disconnect",
                    "remote connected; button still Disconnect");

                SerialStatus(form).Text.Should().Be("Connected");
            }
        });
    }

    /// <summary>
    /// Scenario: form closing during held remote start.
    /// Expected: serial closes.
    /// </summary>
    [Fact]
    public async Task FormClosing_DuringRemoteStart_ClosesSerial()
    {
        await StaTask.Run(async () =>
        {
            var (form, serial, remote) = CreateForm("COM6");
            remote.HoldNextStart();
            using (form)
            {
                form.Show();
                await WaitForUiAsync(() => ConnectButton(form).Enabled, "ports loaded");

                try
                {
                    await ClickConnectAsync(form);
                    await WaitForUiAsync(() => serial.IsOpen, "serial open");

                    form.Close();
                    await WaitForUiAsync(
                        () => form.IsDisposed,
                        "closing during remote start must complete",
                        TimeSpan.FromSeconds(10));

                    serial.IsOpen.Should().BeFalse();
                }
                finally
                {
                    remote.ReleaseHeldStart();
                }
            }
        });
    }
}
