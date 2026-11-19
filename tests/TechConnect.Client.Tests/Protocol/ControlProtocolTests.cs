// -----------------------------------------------------------------------------
// Project: TechConnect.Client.Tests
// File:    ControlProtocolTests.cs
//
// Purpose:
// Verifies TechConnect control-protocol JSON serialize/parse round-trips, version
// rejection, and baud allowlist.
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using TechConnect.Protocol;

namespace TechConnect.Client.Tests.Protocol;

/// <summary>
/// Unit tests for <see cref="ControlProtocol"/> message create/serialize/parse and baud allowlist.
/// </summary>
public sealed class ControlProtocolTests
{
    /// <summary>
    /// Scenario: a connector.hello message with port and baud payload is serialized then parsed.
    /// Expected: parse succeeds, type/version match, and payload fields round-trip intact.
    /// </summary>
    [Fact]
    public void Serialize_round_trips_hello()
    {
        var message = ControlProtocol.Create(
            ControlProtocol.TypeConnectorHello,
            new ConnectorHelloPayload
            {
                ClientSessionId = "abc",
                Hostname = "HOST",
                PortName = "COM3",
                Config = new SerialConfigPayload { BaudRate = 115200 }
            },
            correlationId: "c1");

        var json = ControlProtocol.Serialize(message);
        ControlProtocol.TryParse(json, out var parsed, out var error).Should().BeTrue(error);
        parsed!.Type.Should().Be(ControlProtocol.TypeConnectorHello);
        parsed.V.Should().Be(1);
        ControlProtocol.TryGetPayload(parsed, out ConnectorHelloPayload? hello, out _).Should().BeTrue();
        hello!.PortName.Should().Be("COM3");
        hello.Config.BaudRate.Should().Be(115200);
    }

    /// <summary>
    /// Scenario: JSON declares an unsupported protocol version (v=99).
    /// Expected: TryParse fails, message is null, and the error mentions version.
    /// </summary>
    [Fact]
    public void Rejects_unsupported_version()
    {
        var json = """{"v":99,"type":"connector.hello","payload":{}}""";
        ControlProtocol.TryParse(json, out var message, out var error).Should().BeFalse();
        message.Should().BeNull();
        error.Should().Contain("version");
    }

    /// <summary>
    /// Scenario: baud rates 9600 and 115200 are allowlisted; 12345 is not.
    /// Expected: <see cref="ControlProtocol.IsSupportedBaudRate"/> matches the InlineData expectation.
    /// </summary>
    [Theory]
    [InlineData(9600, true)]
    [InlineData(115200, true)]
    [InlineData(12345, false)]
    public void Baud_allowlist(int baud, bool expected) =>
        ControlProtocol.IsSupportedBaudRate(baud).Should().Be(expected);
}