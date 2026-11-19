// -----------------------------------------------------------------------------
// Project: TechConnect.Client.Tests
// File:    SerialPortDiscoveryServiceTests.cs
//
// Purpose:
// Verifies serial port discovery sorting, de-duplication, friendly-name enrichment, availability checks, and caption parsing.
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using TechConnect.Client.Services;
using TechConnect.Client.Tests.Support;

namespace TechConnect.Client.Tests.Discovery;

/// <summary>
/// Unit tests for serial port discovery sorting, enrichment, availability, and caption parsing.
/// </summary>
public sealed class SerialPortDiscoveryServiceTests
{
    /// <summary>
    /// Builds a SerialPortDiscoveryService over the given fake enumerator and optional friendly-name lookup.
    /// </summary>
    private static SerialPortDiscoveryService CreateService(
        FakeSerialPortEnumerator enumerator,
        FakeFriendlyNameLookup? lookup = null)
    {
        return new SerialPortDiscoveryService(
            new CollectingLogger<SerialPortDiscoveryService>(),
            enumerator,
            lookup ?? new FakeFriendlyNameLookup());
    }

    /// <summary>
    /// Scenario: enumerator returns no ports.
    /// Expected: GetAvailablePorts is empty.
    /// </summary>
    [Fact]
    public void GetAvailablePorts_None_ReturnsEmpty()
    {
        var service = CreateService(new FakeSerialPortEnumerator());
        service.GetAvailablePorts().Should().BeEmpty();
    }

    /// <summary>
    /// Scenario: enumerator returns COM3.
    /// Expected: single port named COM3.
    /// </summary>
    [Fact]
    public void GetAvailablePorts_OnePort_ReturnsIt()
    {
        var service = CreateService(new FakeSerialPortEnumerator("COM3"));
        var ports = service.GetAvailablePorts();
        ports.Should().ContainSingle().Which.PortName.Should().Be("COM3");
    }

    /// <summary>
    /// Scenario: ports COM10/COM2/COM1/COM20.
    /// Expected: natural COM order COM1, COM2, COM10, COM20.
    /// </summary>
    [Fact]
    public void GetAvailablePorts_ManyPorts_NaturalComSort()
    {
        var service = CreateService(new FakeSerialPortEnumerator("COM10", "COM2", "COM1", "COM20"));
        var names = service.GetAvailablePorts().Select(p => p.PortName).ToArray();
        names.Should().Equal("COM1", "COM2", "COM10", "COM20");
    }

    /// <summary>
    /// Scenario: case/whitespace duplicate COM3 entries plus COM4.
    /// Expected: unique COM3 then COM4.
    /// </summary>
    [Fact]
    public void GetAvailablePorts_Duplicates_Collapsed()
    {
        var service = CreateService(new FakeSerialPortEnumerator("COM3", "com3", " COM3 ", "COM4"));
        var names = service.GetAvailablePorts().Select(p => p.PortName).ToArray();
        names.Should().Equal("COM3", "COM4");
    }

    /// <summary>
    /// Scenario: port present but lookup empty.
    /// Expected: FriendlyName null; DisplayName is the port name.
    /// </summary>
    [Fact]
    public void GetAvailablePorts_MissingFriendlyNames_LeavesNull()
    {
        var service = CreateService(new FakeSerialPortEnumerator("COM5"), new FakeFriendlyNameLookup());
        var port = service.GetAvailablePorts().Should().ContainSingle().Subject;
        port.FriendlyName.Should().BeNull();
        port.DisplayName.Should().Be("COM5");
    }

    /// <summary>
    /// Scenario: lookup maps COM7 to a friendly name.
    /// Expected: FriendlyName set, DisplayName includes both, lookup invoked once.
    /// </summary>
    [Fact]
    public void GetAvailablePorts_EnrichesFriendlyNames()
    {
        var lookup = new FakeFriendlyNameLookup(new Dictionary<string, string>
        {
            ["COM7"] = "USB Serial Device"
        });
        var service = CreateService(new FakeSerialPortEnumerator("COM7"), lookup);

        var port = service.GetAvailablePorts().Should().ContainSingle().Subject;
        port.FriendlyName.Should().Be("USB Serial Device");
        port.DisplayName.Should().Contain("COM7");
        port.DisplayName.Should().Contain("USB Serial Device");
        lookup.LookupCount.Should().Be(1);
    }

    /// <summary>
    /// Scenario: two ports share the same friendly name.
    /// Expected: both ports returned with that friendly name.
    /// </summary>
    [Fact]
    public void GetAvailablePorts_DuplicateFriendlyNames_AreAllowed()
    {
        var lookup = new FakeFriendlyNameLookup(new Dictionary<string, string>
        {
            ["COM1"] = "Shared Device",
            ["COM2"] = "Shared Device"
        });
        var service = CreateService(new FakeSerialPortEnumerator("COM1", "COM2"), lookup);
        var ports = service.GetAvailablePorts();
        ports.Should().HaveCount(2);
        ports.Select(p => p.FriendlyName).Should().OnlyContain(n => n == "Shared Device");
    }

    /// <summary>
    /// Scenario: enumerator throws UnauthorizedAccessException.
    /// Expected: InvalidOperationException mentioning enumerate with that inner exception.
    /// </summary>
    [Fact]
    public void GetAvailablePorts_EnumeratorFailure_ThrowsInvalidOperation()
    {
        var enumerator = new FakeSerialPortEnumerator("COM1")
        {
            ThrowOnEnumerate = new UnauthorizedAccessException("denied")
        };
        var service = CreateService(enumerator);

        var act = () => service.GetAvailablePorts();
        var ex = act.Should().Throw<InvalidOperationException>()
            .WithMessage("*enumerate*");
        ex.Which.InnerException.Should().BeOfType<UnauthorizedAccessException>();
    }

    /// <summary>
    /// Scenario: only COM9 is enumerated.
    /// Expected: com9 true; COM8 and blank false.
    /// </summary>
    [Fact]
    public void IsPortAvailable_MatchesIgnoringCase()
    {
        var service = CreateService(new FakeSerialPortEnumerator("COM9"));
        service.IsPortAvailable("com9").Should().BeTrue();
        service.IsPortAvailable("COM8").Should().BeFalse();
        service.IsPortAvailable(" ").Should().BeFalse();
    }

    /// <summary>
    /// Scenario: enumerator throws during availability check.
    /// Expected: IsPortAvailable returns false rather than throwing.
    /// </summary>
    [Fact]
    public void IsPortAvailable_EnumeratorFailure_ReturnsFalse()
    {
        var enumerator = new FakeSerialPortEnumerator("COM1")
        {
            ThrowOnEnumerate = new InvalidOperationException("boom")
        };
        // EnumeratePortNames wraps into InvalidOperationException; IsPortAvailable catches that.
        enumerator.ThrowOnEnumerate = new Exception("raw");
        var service = CreateService(enumerator);
        service.IsPortAvailable("COM1").Should().BeFalse();
    }

    /// <summary>
    /// Scenario: WMI-style captions with and without (COMn).
    /// Expected: InlineData port/friendly extraction or false when no port.
    /// </summary>
    [Theory]
    [InlineData("Intel(R) AMT - SOL (COM3)", "COM3", "Intel(R) AMT - SOL")]
    [InlineData("USB Serial Device (COM12)", "COM12", "USB Serial Device")]
    [InlineData("COM5", null, null)]
    [InlineData("Something without port", null, null)]
    public void TryParseCaption_ExtractsPortAndCleansName(string caption, string? expectedPort, string? expectedFriendly)
    {
        var ok = SerialPortFriendlyNameLookup.TryParseCaption(caption, out var port, out var friendly);
        if (expectedPort is null)
        {
            ok.Should().BeFalse();
            return;
        }

        ok.Should().BeTrue();
        port.Should().Be(expectedPort);
        friendly.Should().Be(expectedFriendly);
    }

    /// <summary>
    /// Scenario: caption collapses to the port name after cleaning.
    /// Expected: parse true, friendly empty.
    /// </summary>
    [Fact]
    public void TryParseCaption_PortOnlyName_YieldsEmptyFriendly()
    {
        // Caption where name collapses to the port itself after cleaning.
        SerialPortFriendlyNameLookup.TryParseCaption("COM7 (COM7)", out var port, out var friendly)
            .Should().BeTrue();
        port.Should().Be("COM7");
        friendly.Should().BeEmpty();
    }

    /// <summary>
    /// Scenario: mixed COM/LPT/null names sorted with ComPortNameComparer.
    /// Expected: null first, natural COM order, then non-COM.
    /// </summary>
    [Fact]
    public void ComPortNameComparer_OrdersNaturally()
    {
        var input = new string?[] { "COM10", "COM2", "COM1", "LPT1", "COM20", null, "com3" };
        var sorted = input.OrderBy(x => x, Comparer<string?>.Create((a, b) => ComPortNameComparer.Instance.Compare(a, b))).ToArray();
        sorted.Should().Equal(null, "COM1", "COM2", "com3", "COM10", "COM20", "LPT1");
    }

    /// <summary>
    /// Scenario: parse COM numbers from names.
    /// Expected: InlineData success/number outcomes.
    /// </summary>
    [Theory]
    [InlineData("COM1", 1, true)]
    [InlineData("com25", 25, true)]
    [InlineData("COM", 0, false)]
    [InlineData("PORT1", 0, false)]
    public void TryParseComNumber(string name, int expected, bool success)
    {
        var ok = ComPortNameComparer.TryParseComNumber(name, out var number);
        ok.Should().Be(success);
        if (success)
        {
            number.Should().Be(expected);
        }
    }
}
