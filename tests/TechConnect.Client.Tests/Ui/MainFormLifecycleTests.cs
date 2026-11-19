// -----------------------------------------------------------------------------
// Project: TechConnect.Client.Tests
// File:    MainFormLifecycleTests.cs
//
// Purpose:
// Verifies MainForm construction, start/stop lifecycle, and disposal on an STA thread.
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using TechConnect.Client.Forms;
using TechConnect.Client.Models;
using TechConnect.Client.Tests.Support;

namespace TechConnect.Client.Tests.Ui;

/// <summary>
/// STA MainForm construction, load, connect toggle, clear, and closing lifecycle tests.
/// </summary>
public sealed class MainFormLifecycleTests
{
    /// <summary>
    /// Creates a MainForm with fake discovery/serial/remote dependencies.
    /// </summary>
    private static (MainForm Form, FakeSerialPortDiscoveryService Discovery, FakeSerialConnectionService Serial, FakeRemoteSessionService Remote)
        CreateForm(FakeSerialPortDiscoveryService? discovery = null)
    {
        discovery ??= new FakeSerialPortDiscoveryService();
        var serial = new FakeSerialConnectionService();
        var remote = new FakeRemoteSessionService();
        var form = new MainForm(
            new CollectingLogger<MainForm>(),
            discovery,
            serial,
            remote);
        return (form, discovery, serial, remote);
    }

    /// <summary>
    /// Reads the private _errorLabel field via reflection.
    /// </summary>
    private static Label ErrorLabel(MainForm form) => UiReflection.GetPrivateField<Label>(form, "_errorLabel");

    /// <summary>
    /// Reads the private _connectButton field via reflection.
    /// </summary>
    private static Button ConnectButton(MainForm form) => UiReflection.GetPrivateField<Button>(form, "_connectButton");

    /// <summary>
    /// Reads the private clear-console button via reflection.
    /// </summary>
    private static Button ClearConsoleButton(MainForm form) =>
        UiReflection.GetPrivateField<Button>(form, "_clearConsoleButton");

    /// <summary>
    /// Reads the private port ComboBox via reflection.
    /// </summary>
    private static ComboBox PortCombo(MainForm form) =>
        UiReflection.GetPrivateField<ComboBox>(form, "_serialPortComboBox");

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
    /// Scenario: construct MainForm with no ports.
    /// Expected: ready message shown; Connect disabled.
    /// </summary>
    [Fact]
    public async Task Constructor_ShowsReadyMessage_AndConnectDisabledWithoutPorts()
    {
        await StaTask.Run(() =>
        {
            var (form, _, _, _) = CreateForm();
            using (form)
            {
                ErrorLabel(form).Text.Should().Be("Select a serial port and click Connect.");
                ConnectButton(form).Enabled.Should().BeFalse();
                ConnectButton(form).Text.Should().Be("Connect");
            }
        });
    }

    /// <summary>
    /// Scenario: Load with empty discovery.
    /// Expected: Connect remains disabled.
    /// </summary>
    [Fact]
    public async Task Load_WithNoPorts_KeepsConnectDisabled()
    {
        await StaTask.Run(async () =>
        {
            var discovery = new FakeSerialPortDiscoveryService();
            discovery.SetPorts(Array.Empty<string>());
            var (form, _, _, _) = CreateForm(discovery);
            using (form)
            {
                form.Show();
                await WaitForUiAsync(
                    () => ErrorLabel(form).Text.Contains("No serial ports", StringComparison.OrdinalIgnoreCase),
                    "expected empty-port status after Load refresh");

                ConnectButton(form).Enabled.Should().BeFalse();
                PortCombo(form).Enabled.Should().BeFalse();
            }
        });
    }

    /// <summary>
    /// Scenario: Load with discovered ports.
    /// Expected: Connect enabled.
    /// </summary>
    [Fact]
    public async Task Load_WithPorts_EnablesConnect()
    {
        await StaTask.Run(async () =>
        {
            var discovery = new FakeSerialPortDiscoveryService();
            discovery.SetPorts("COM3", "COM4");
            var (form, _, _, _) = CreateForm(discovery);
            using (form)
            {
                form.Show();
                await WaitForUiAsync(
                    () => ConnectButton(form).Enabled,
                    "Connect should enable once a real port is selected");

                ConnectButton(form).Text.Should().Be("Connect");
                ErrorLabel(form).Text.Should().Be("Select a serial port and click Connect.");
                PortCombo(form).SelectedItem.Should().BeOfType<SerialPortInfo>();
            }
        });
    }

    /// <summary>
    /// Scenario: connect clicked twice rapidly.
    /// Expected: second click disconnects; serial not opened twice.
    /// </summary>
    [Fact]
    public async Task DoubleClickConnect_SecondClickDisconnects_DoesNotOpenTwice()
    {
        await StaTask.Run(async () =>
        {
            var discovery = new FakeSerialPortDiscoveryService();
            discovery.SetPorts("COM9");
            var (form, _, serial, remote) = CreateForm(discovery);
            using (form)
            {
                serial.HoldNextOpen();
                form.Show();
                await WaitForUiAsync(() => ConnectButton(form).Enabled, "port list should load");

                var click = typeof(MainForm).GetMethod(
                    "ConnectButton_Click",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("ConnectButton_Click not found.");

                try
                {
                    click.Invoke(form, [ConnectButton(form), EventArgs.Empty]);
                    await WaitForUiAsync(
                        () => serial.OpenCallCount >= 1 && ConnectButton(form).Text == "Disconnect",
                        "first click should reach OpenAsync");

                    // Second click while serial session is active is Disconnect.
                    click.Invoke(form, [ConnectButton(form), EventArgs.Empty]);
                    await WaitForUiAsync(
                        () => ConnectButton(form).Text == "Connect",
                        "second click disconnects / cancels");

                    serial.OpenCallCount.Should().Be(1, "must not start a second OpenAsync");
                    serial.IsOpen.Should().BeFalse();
                }
                finally
                {
                    serial.ReleaseHeldOpen();
                }
            }
        });
    }

    /// <summary>
    /// Scenario: Clear Console button clicked.
    /// Expected: no throw.
    /// </summary>
    [Fact]
    public async Task ClearConsole_DoesNotThrow()
    {
        await StaTask.Run(async () =>
        {
            var discovery = new FakeSerialPortDiscoveryService();
            discovery.SetPorts("COM1");
            var (form, _, _, _) = CreateForm(discovery);
            using (form)
            {
                form.Show();
                await WaitForUiAsync(() => ConnectButton(form).Enabled, "ports loaded");

                var act = () => ClearConsoleButton(form).PerformClick();
                act.Should().NotThrow();
            }
        });
    }

    /// <summary>
    /// Scenario: close form while connected.
    /// Expected: remote session Stop is invoked.
    /// </summary>
    [Fact]
    public async Task FormClosing_WhileConnected_StopsRemoteSession()
    {
        await StaTask.Run(async () =>
        {
            var discovery = new FakeSerialPortDiscoveryService();
            discovery.SetPorts("COM5");
            var (form, _, serial, remote) = CreateForm(discovery);
            using (form)
            {
                form.Show();
                await WaitForUiAsync(() => ConnectButton(form).Enabled, "ports loaded");

                ConnectButton(form).PerformClick();
                await WaitForUiAsync(
                    () => serial.IsOpen && remote.StartCount >= 1,
                    "connect should open serial and start remote session");

                form.Close();
                await WaitForUiAsync(
                    () => remote.StopCount >= 1 && form.IsDisposed,
                    "FormClosing should stop the remote session and dispose the form",
                    TimeSpan.FromSeconds(10));

                remote.StopCount.Should().BeGreaterThanOrEqualTo(1);
                serial.IsOpen.Should().BeFalse();
            }
        });
    }
}
