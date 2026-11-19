// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    CliCommandParserTests.cs
//
// Purpose:
// Regression tests and helpers covering CliCommandParser behaviour.
// Fixture: CliCommandParserTests.
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using TechConnect.Server.Authorization;

namespace TechConnect.Server.Tests.Authorization;
/// <summary>
/// xUnit test fixture for CliCommandParser.
/// </summary>

public sealed class CliCommandParserTests
{
    /// <summary>
    /// Verifies Resolves Known Leaf Commands.
    /// </summary>
    [Theory]
    [InlineData("help", (int)CliCommandId.Help)]
    [InlineData("?", (int)CliCommandId.Help)]
    [InlineData("exit", (int)CliCommandId.Exit)]
    [InlineData("quit", (int)CliCommandId.Exit)]
    [InlineData("attach", (int)CliCommandId.Attach)]
    [InlineData("connect abc", (int)CliCommandId.Connect)]
    [InlineData("connect abc baud 115200", (int)CliCommandId.Connect)]
    [InlineData("connect abc baud 19200 databits 7 parity even stopbits 2", (int)CliCommandId.Connect)]
    [InlineData("show sessions", (int)CliCommandId.ShowSessions)]
    [InlineData("show sessions detail", (int)CliCommandId.ShowSessionsDetail)]
    [InlineData("show session abc", (int)CliCommandId.ShowSession)]
    [InlineData("show serial", (int)CliCommandId.ShowSerial)]
    [InlineData("show history", (int)CliCommandId.ShowHistory)]
    [InlineData("show history session abc", (int)CliCommandId.ShowHistorySession)]
    [InlineData("show history user alice", (int)CliCommandId.ShowHistoryUser)]
    [InlineData("show version", (int)CliCommandId.ShowVersion)]
    [InlineData("show privilege", (int)CliCommandId.ShowPrivilege)]
    [InlineData("show clock", (int)CliCommandId.ShowClock)]
    [InlineData("show terminal", (int)CliCommandId.ShowTerminal)]
    [InlineData("show users", (int)CliCommandId.ShowUsers)]
    [InlineData("show dns", (int)CliCommandId.ShowDns)]
    [InlineData("show ip interfaces", (int)CliCommandId.ShowIpInterfaces)]
    [InlineData("show ip route", (int)CliCommandId.ShowIpRoute)]
    [InlineData("clear screen", (int)CliCommandId.ClearScreen)]
    [InlineData("nslookup example.com", (int)CliCommandId.Nslookup)]
    [InlineData("disconnect", (int)CliCommandId.DisconnectSelf)]
    [InlineData("disconnect abcd1234", (int)CliCommandId.Disconnect)]
    [InlineData("traceroute 8.8.8.8", (int)CliCommandId.Traceroute)]
    [InlineData("traceroute 2001:db8::1", (int)CliCommandId.Traceroute)]
    [InlineData("traceroute 8.8.8.8 ttl 20", (int)CliCommandId.Traceroute)]
    [InlineData("copy http://h/f.bin serial:abc", (int)CliCommandId.Copy)]
    [InlineData("copy xmodem: serial:abc", (int)CliCommandId.Copy)]
    public void Resolves_KnownLeafCommands(string input, int expected)
    {
        var result = CliCommandParser.Parse(input);
        result.Status.Should().Be(CliParseStatus.Resolved);
        result.CommandId.Should().Be((CliCommandId)expected);
        CliCommandParser.TryResolve(input, out var id, out _).Should().BeTrue();
        id.Should().Be((CliCommandId)expected);
    }
    /// <summary>
    /// Verifies Incomplete Intermediate Or Missing Argument.
    /// </summary>

    [Theory]
    [InlineData("show")]
    [InlineData("show session")]
    [InlineData("show history session")]
    [InlineData("show history user")]
    [InlineData("connect")]
    [InlineData("traceroute")]
    public void Incomplete_IntermediateOrMissingArgument(string input)
    {
        var result = CliCommandParser.Parse(input);
        result.Status.Should().Be(CliParseStatus.Incomplete);
        CliCommandParser.TryResolve(input, out _, out _).Should().BeFalse();
    }
    /// <summary>
    /// Verifies Rejects Unknown Commands.
    /// </summary>

    [Theory]
    [InlineData("foobar")]
    [InlineData("set")]
    [InlineData("set foo")]
    [InlineData("set baud 9600")]
    [InlineData("detach")]
    [InlineData("terminal")]
    [InlineData("attach abc")]
    [InlineData("show widgets")]
    [InlineData("shwo sessions")]
    public void Rejects_UnknownCommands(string input)
    {
        var result = CliCommandParser.Parse(input);
        result.Status.Should().Be(CliParseStatus.Invalid);
        CliCommandParser.TryResolve(input, out _, out _).Should().BeFalse();
    }
    /// <summary>
    /// Verifies Catalog Contains Every Executable Command.
    /// </summary>

    [Fact]
    public void Catalog_ContainsEveryExecutableCommand()
    {
        var ids = new HashSet<CliCommandId>();
        void Walk(CliCommandNode node)
        {
            if (node.CommandId is { } id)
            {
                ids.Add(id);
            }

            if (node.CommandIdWithArgument is { } withArg)
            {
                ids.Add(withArg);
            }

            foreach (var child in node.Children)
            {
                Walk(child);
            }
        }

        foreach (var root in CliCommandCatalog.Roots)
        {
            Walk(root);
        }

        ids.Should().Contain(CliCommandId.Help);
        ids.Should().Contain(CliCommandId.Exit);
        ids.Should().Contain(CliCommandId.ClearScreen);
        ids.Should().Contain(CliCommandId.ShowSessions);
        ids.Should().Contain(CliCommandId.ShowSessionsDetail);
        ids.Should().Contain(CliCommandId.ShowSession);
        ids.Should().Contain(CliCommandId.ShowSerial);
        ids.Should().Contain(CliCommandId.ShowHistory);
        ids.Should().Contain(CliCommandId.ShowHistorySession);
        ids.Should().Contain(CliCommandId.ShowHistoryUser);
        ids.Should().Contain(CliCommandId.ShowVersion);
        ids.Should().Contain(CliCommandId.ShowPrivilege);
        ids.Should().Contain(CliCommandId.ShowClock);
        ids.Should().Contain(CliCommandId.ShowTerminal);
        ids.Should().Contain(CliCommandId.ShowUsers);
        ids.Should().Contain(CliCommandId.ShowDns);
        ids.Should().Contain(CliCommandId.ShowIpInterfaces);
        ids.Should().Contain(CliCommandId.ShowIpRoute);
        ids.Should().Contain(CliCommandId.Nslookup);
        ids.Should().Contain(CliCommandId.Connect);
        ids.Should().Contain(CliCommandId.Attach);
        ids.Should().Contain(CliCommandId.Disconnect);
        ids.Should().Contain(CliCommandId.DisconnectSelf);
    }
}
