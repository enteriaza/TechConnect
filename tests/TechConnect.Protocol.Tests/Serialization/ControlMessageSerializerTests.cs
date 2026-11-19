// -----------------------------------------------------------------------------
// Project: TechConnect.Protocol.Tests
// File:    ControlMessageSerializerTests.cs
//
// Purpose:
// Verifies UTF-8 JSON control-message serialization, validation rules, serial-data
// opacity, and stable MessageType wire identities.
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using TechConnect.Protocol;
using TechConnect.Protocol.Messages;
using TechConnect.Protocol.Serialization;

namespace TechConnect.Protocol.Tests.Serialization;

/// <summary>
/// Regression tests for <see cref="ControlMessageSerializer"/> control JSON and serial-data helpers.
/// </summary>
public sealed class ControlMessageSerializerTests
{
    /// <summary>
    /// Verifies that SessionPing and SessionPingResult DTOs survive serialize/deserialize round-trips.
    /// </summary>
    [Fact]
    public void SessionPing_RoundTrips()
    {
        var request = ControlMessageSerializer.DeserializeSessionPing(
            ControlMessageSerializer.SerializeSessionPing(new SessionPingMessage
            {
                CorrelationId = "probe-1"
            }));
        request.CorrelationId.Should().Be("probe-1");

        var result = ControlMessageSerializer.DeserializeSessionPingResult(
            ControlMessageSerializer.SerializeSessionPingResult(new SessionPingResultMessage
            {
                CorrelationId = "probe-1",
                Success = true,
                Hostname = "CHRIS-PC"
            }));
        result.Success.Should().BeTrue();
        result.Hostname.Should().Be("CHRIS-PC");
    }

    /// <summary>
    /// Verifies that a fully populated HelloMessage round-trips with equivalent field values.
    /// </summary>
    [Fact]
    public void Hello_RoundTrips()
    {
        var original = new HelloMessage
        {
            CorrelationId = "c1",
            ClientSessionId = "session",
            Hostname = "host",
            ApplicationVersion = "1.0.0",
            PortName = "COM3",
            BaudRate = 115200,
            DataBits = 8,
            Parity = "None",
            StopBits = "One",
            Handshake = "None"
        };

        var bytes = ControlMessageSerializer.SerializeHello(original);
        var restored = ControlMessageSerializer.DeserializeHello(bytes);
        restored.Should().BeEquivalentTo(original);
    }

    /// <summary>
    /// Verifies round-trips for HelloResult, SetBaud, SerialConfiguration, SetBaudResult,
    /// Error, and Disconnect control messages, including failure and success result shapes.
    /// </summary>
    [Fact]
    public void HelloResult_SetBaud_SerialConfiguration_Error_Disconnect_RoundTrip()
    {
        var helloResult = ControlMessageSerializer.DeserializeHelloResult(
            ControlMessageSerializer.SerializeHelloResult(new HelloResultMessage
            {
                Success = true,
                ServerName = "TechConnect.Server",
                SessionId = "a82f31c2",
                CorrelationId = "x"
            }));
        helloResult.Success.Should().BeTrue();
        helloResult.SessionId.Should().Be("a82f31c2");

        var setBaud = ControlMessageSerializer.DeserializeSetBaud(
            ControlMessageSerializer.SerializeSetBaud(new SetBaudMessage { BaudRate = 9600, CorrelationId = "b" }));
        setBaud.BaudRate.Should().Be(9600);
        setBaud.CorrelationId.Should().Be("b");

        var serialConfig = ControlMessageSerializer.DeserializeSerialConfiguration(
            ControlMessageSerializer.SerializeSerialConfiguration(new SerialConfigurationMessage
            {
                BaudRate = 115200,
                DataBits = 8,
                Parity = "None",
                StopBits = "One",
                Handshake = "None",
                CorrelationId = "c"
            }));
        serialConfig.BaudRate.Should().Be(115200);
        serialConfig.CorrelationId.Should().Be("c");

        var setBaudResult = ControlMessageSerializer.DeserializeSetBaudResult(
            ControlMessageSerializer.SerializeSetBaudResult(new SetBaudResultMessage
            {
                Success = false,
                Error = "denied"
            }));
        setBaudResult.Error.Should().Be("denied");

        var setBaudOk = ControlMessageSerializer.DeserializeSetBaudResult(
            ControlMessageSerializer.SerializeSetBaudResult(new SetBaudResultMessage
            {
                Success = true,
                CorrelationId = "ok",
                Config = new SerialConfigurationMessage
                {
                    BaudRate = 9600,
                    DataBits = 8,
                    Parity = "None",
                    StopBits = "One",
                    Handshake = "None"
                }
            }));
        setBaudOk.Success.Should().BeTrue();
        setBaudOk.Config!.BaudRate.Should().Be(9600);

        var error = ControlMessageSerializer.DeserializeError(
            ControlMessageSerializer.SerializeError(new ErrorMessage { Code = "x", Message = "y" }));
        error.Code.Should().Be("x");

        var disconnect = ControlMessageSerializer.DeserializeDisconnect(
            ControlMessageSerializer.SerializeDisconnect(new DisconnectMessage { Reason = "bye" }));
        disconnect.Reason.Should().Be("bye");
    }

    /// <summary>
    /// Verifies that malformed JSON payloads raise <see cref="ProtocolInvalidPayloadException"/>.
    /// </summary>
    [Fact]
    public void InvalidJson_Rejected()
    {
        var act = () => ControlMessageSerializer.DeserializeHello("{not-json"u8);
        act.Should().Throw<ProtocolInvalidPayloadException>();
    }

    /// <summary>
    /// Verifies that a Hello payload missing required clientSessionId is rejected.
    /// </summary>
    [Fact]
    public void MissingRequiredFields_Rejected()
    {
        var act = () => ControlMessageSerializer.DeserializeHello("""{"hostname":"x"}"""u8);
        act.Should().Throw<ProtocolInvalidPayloadException>().WithMessage("*clientSessionId*");
    }

    /// <summary>
    /// Verifies that SetBaud rejects a baudRate below the minimum of 1.
    /// </summary>
    [Fact]
    public void InvalidFieldValues_Rejected()
    {
        var act = () => ControlMessageSerializer.DeserializeSetBaud("""{"baudRate":0}"""u8);
        act.Should().Throw<ProtocolInvalidPayloadException>().WithMessage("*baudRate*");
    }

    /// <summary>
    /// Verifies that CopySerialData preserves every octet, including awkward binary sequences.
    /// </summary>
    [Fact]
    public void SerialData_PreservesAllByteValues()
    {
        var payload = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        ControlMessageSerializer.CopySerialData(payload).Should().Equal(payload);

        // Include CR/LF, NUL, 0xFF, ESC, and invalid UTF-8 sequences as opaque bytes.
        var awkward = new byte[] { 0x00, 0xFF, 0x0D, 0x0A, 0x1B, 0x5B, 0x41, 0xC3, 0x28 };
        ControlMessageSerializer.CopySerialData(awkward).Should().Equal(awkward);
    }

    /// <summary>
    /// Verifies that routing SerialData through the control Serialize dispatcher is rejected.
    /// </summary>
    [Fact]
    public void Serialize_SerialDataViaControlApi_Rejected()
    {
        var act = () => ControlMessageSerializer.Serialize(MessageType.SerialData, new object());
        act.Should().Throw<ProtocolInvalidPayloadException>();
    }

    /// <summary>
    /// Verifies that MessageType wire identities remain at their fixed numeric assignments.
    /// </summary>
    [Fact]
    public void OperatorStatus_MessageTypeIs0x30()
    {
        ((byte)MessageType.OperatorStatus).Should().Be(0x30);
        // Existing identities must remain stable.
        ((byte)MessageType.Hello).Should().Be(0x01);
        ((byte)MessageType.HelloResult).Should().Be(0x02);
        ((byte)MessageType.SerialData).Should().Be(0x10);
        ((byte)MessageType.SerialConfiguration).Should().Be(0x11);
        ((byte)MessageType.SetBaud).Should().Be(0x20);
        ((byte)MessageType.SetBaudResult).Should().Be(0x21);
        ((byte)MessageType.Error).Should().Be(0x7E);
        ((byte)MessageType.Disconnect).Should().Be(0x7F);
    }

    /// <summary>
    /// Verifies that OperatorStatusMessage fields survive a serialize/deserialize round-trip.
    /// </summary>
    [Fact]
    public void OperatorStatus_RoundTrips()
    {
        var original = new OperatorStatusMessage
        {
            StateVersion = 3,
            Attached = true,
            TerminalActive = true
        };

        var bytes = ControlMessageSerializer.SerializeOperatorStatus(original);
        var restored = ControlMessageSerializer.DeserializeOperatorStatus(bytes);
        restored.Should().BeEquivalentTo(original);
    }

    /// <summary>
    /// Verifies that OperatorStatus rejects terminalActive when attached is false.
    /// </summary>
    [Fact]
    public void OperatorStatus_RejectsTerminalActiveWithoutAttached()
    {
        var act = () => ControlMessageSerializer.SerializeOperatorStatus(new OperatorStatusMessage
        {
            StateVersion = 1,
            Attached = false,
            TerminalActive = true
        });
        act.Should().Throw<ProtocolInvalidPayloadException>().WithMessage("*terminalActive*");
    }

    /// <summary>
    /// Verifies that OperatorStatus rejects a zero stateVersion.
    /// </summary>
    [Fact]
    public void OperatorStatus_RejectsZeroVersion()
    {
        var act = () => ControlMessageSerializer.DeserializeOperatorStatus(
            """{"stateVersion":0,"attached":false,"terminalActive":false}"""u8);
        act.Should().Throw<ProtocolInvalidPayloadException>().WithMessage("*stateVersion*");
    }
}
