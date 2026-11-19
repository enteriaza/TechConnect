// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    CliCommandPolicyTests.cs
//
// Purpose:
// Regression tests and helpers covering CliCommandPolicy behaviour.
// Fixture: CliCommandPolicyTests.
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using TechConnect.Server.Authentication.Models;
using TechConnect.Server.Authorization;

namespace TechConnect.Server.Tests.Authorization;
/// <summary>
/// xUnit test fixture for CliCommandPolicy.
/// </summary>

public sealed class CliCommandPolicyTests
{
    /// <summary>Phase 4 minimums: command → min privilege.</summary>
    public static IEnumerable<object[]> CommandMinimums()
    {
        yield return [(int)CliCommandId.Help, 1];
        yield return [(int)CliCommandId.Exit, 1];
        yield return [(int)CliCommandId.ClearScreen, 1];
        yield return [(int)CliCommandId.ShowVersion, 1];
        yield return [(int)CliCommandId.ShowPrivilege, 1];
        yield return [(int)CliCommandId.ShowClock, 1];
        yield return [(int)CliCommandId.ShowTerminal, 1];
        yield return [(int)CliCommandId.ShowUsers, 1];
        yield return [(int)CliCommandId.ShowSessions, 1];
        yield return [(int)CliCommandId.ShowSessionsDetail, 1];
        yield return [(int)CliCommandId.ShowSession, 1];
        yield return [(int)CliCommandId.ShowHistory, 1];
        yield return [(int)CliCommandId.ShowHistorySession, 5];
        yield return [(int)CliCommandId.ShowHistoryUser, 11];
        yield return [(int)CliCommandId.ShowDns, 1];
        yield return [(int)CliCommandId.ShowIpInterfaces, 1];
        yield return [(int)CliCommandId.ShowIpRoute, 1];
        yield return [(int)CliCommandId.Nslookup, 1];
        yield return [(int)CliCommandId.Ping, 1];
        yield return [(int)CliCommandId.Traceroute, 1];
        yield return [(int)CliCommandId.Copy, 1];
        yield return [(int)CliCommandId.ShowSerial, 7];
        yield return [(int)CliCommandId.Connect, 1];
        yield return [(int)CliCommandId.Attach, 1];
        yield return [(int)CliCommandId.DisconnectSelf, 1];
        yield return [(int)CliCommandId.Enable, 1];
        yield return [(int)CliCommandId.Disable, 1];
        yield return [(int)CliCommandId.Disconnect, 7];
    }
    /// <summary>
    /// Verifies Boundary Immediately Below Denied At And Above Allowed.
    /// </summary>

    [Theory]
    [MemberData(nameof(CommandMinimums))]
    public void Boundary_ImmediatelyBelow_Denied_AtAndAbove_Allowed(int command, int minimum)
    {
        var id = (CliCommandId)command;
        if (minimum > PrivilegeLevel.Minimum)
        {
            CliCommandPolicy.IsAllowed(PrivilegeLevel.FromInt(minimum - 1), id).Should().BeFalse();
        }

        CliCommandPolicy.IsAllowed(PrivilegeLevel.FromInt(minimum), id).Should().BeTrue();
        if (minimum < PrivilegeLevel.Maximum)
        {
            CliCommandPolicy.IsAllowed(PrivilegeLevel.FromInt(minimum + 1), id).Should().BeTrue();
        }

        CliCommandPolicy.IsAllowed(PrivilegeLevel.FromInt(PrivilegeLevel.Maximum), id).Should().BeTrue();
    }
    /// <summary>
    /// Verifies Cumulative Levels.
    /// </summary>

    public static IEnumerable<object[]> CumulativeLevels()
    {
        foreach (var level in new[] { 1, 2, 3, 5, 7, 8, 11, 14, 15 })
        {
            foreach (var row in CommandMinimums())
            {
                var command = (int)row[0]!;
                var minimum = (int)row[1]!;
                yield return [level, command, level >= minimum];
            }
        }
    }
    /// <summary>
    /// Verifies Cumulative Representative Levels.
    /// </summary>

    [Theory]
    [MemberData(nameof(CumulativeLevels))]
    public void Cumulative_RepresentativeLevels(int privilege, int command, bool expected)
    {
        CliCommandPolicy.IsAllowed(PrivilegeLevel.FromInt(privilege), (CliCommandId)command)
            .Should().Be(expected);
    }
    /// <summary>
    /// Verifies Undefined Command Is Denied.
    /// </summary>

    [Fact]
    public void UndefinedCommand_IsDenied()
    {
        CliCommandPolicy.IsDefined((CliCommandId)999).Should().BeFalse();
        CliCommandPolicy.IsAllowed(PrivilegeLevel.AdministratorBoundary, (CliCommandId)999).Should().BeFalse();
    }
    /// <summary>
    /// Verifies Invalid Privilege Value Is Denied.
    /// </summary>

    [Fact]
    public void InvalidPrivilegeValue_IsDenied()
    {
        CliCommandPolicy.IsAllowed(default, CliCommandId.Help).Should().BeFalse();
    }
    /// <summary>
    /// Verifies Help Level1 Includes Connect Attach Disconnect Self Not Admin Serial.
    /// </summary>

    [Fact]
    public void Help_Level1_IncludesConnectAttachDisconnectSelf_NotAdminSerial()
    {
        var help = CliCommandPolicy.BuildHelpText(PrivilegeLevel.FromInt(1));
        help.Should().Contain("show sessions");
        help.Should().Contain("show session <session-id>");
        help.Should().Contain("help, ?");
        help.Should().Contain("connect <session-id>");
        help.Should().MatchRegex(@"(?m)^\s*attach\b");
        help.Should().Contain("enable");
        help.Should().Contain("disable");
        help.Should().NotMatchRegex(@"(?m)^\s*detach\b");
        help.Should().NotMatchRegex(@"(?m)^\s*terminal\b");
        help.Should().MatchRegex(@"(?m)^\s*disconnect\b");
        help.Should().NotContain("disconnect <session-id>");
        help.Should().Contain("show history");
        help.Should().Contain("show version");
        help.Should().Contain("show users");
        help.Should().Contain("nslookup");
        help.Should().Contain("clear screen");
        help.Should().NotContain("show history session <session-id>");
        help.Should().NotContain("show serial");
        help.Should().NotContain("set baud");
    }
    /// <summary>
    /// Verifies Help Level3 Includes Own History Not Session History Or Serial.
    /// </summary>

    [Fact]
    public void Help_Level3_IncludesOwnHistory_NotSessionHistoryOrSerial()
    {
        var help = CliCommandPolicy.BuildHelpText(PrivilegeLevel.FromInt(3));
        help.Should().Contain("show sessions");
        help.Should().Contain("show session <session-id>");
        help.Should().Contain("connect <session-id>");
        help.Should().MatchRegex(@"(?m)^\s*attach\b");
        help.Should().Contain("show history");
        help.Should().NotContain("show history session <session-id>");
        help.Should().NotContain("show serial");
        help.Should().NotContain("disconnect <session-id>");
    }
    /// <summary>
    /// Verifies Help Level5 Includes History Session Not History User Or Serial.
    /// </summary>

    [Fact]
    public void Help_Level5_IncludesHistorySession_NotHistoryUserOrSerial()
    {
        var help = CliCommandPolicy.BuildHelpText(PrivilegeLevel.FromInt(5));
        help.Should().Contain("show history");
        help.Should().Contain("show history session <session-id>");
        help.Should().Contain("connect <session-id>");
        help.Should().MatchRegex(@"(?m)^\s*attach\b");
        help.Should().NotContain("show history user <username>");
        help.Should().NotContain("show serial");
    }
    /// <summary>
    /// Verifies Help Level7 Includes Operator Commands Including Admin Disconnect.
    /// </summary>

    [Fact]
    public void Help_Level7_IncludesOperatorCommandsIncludingAdminDisconnect()
    {
        var help = CliCommandPolicy.BuildHelpText(PrivilegeLevel.FromInt(7));
        help.Should().Contain("show serial");
        help.Should().Contain("connect <session-id>");
        help.Should().MatchRegex(@"(?m)^\s*attach\b");
        help.Should().NotContain("set baud");
        help.Should().NotMatchRegex(@"(?m)^\s*detach\b");
        help.Should().Contain("disconnect <session-id>");
        help.Should().NotContain("show history user <username>");
    }
    /// <summary>
    /// Verifies Help Level11 Includes History User And Admin Disconnect.
    /// </summary>

    [Fact]
    public void Help_Level11_IncludesHistoryUserAndAdminDisconnect()
    {
        var help = CliCommandPolicy.BuildHelpText(PrivilegeLevel.FromInt(11));
        help.Should().Contain("show history user <username>");
        help.Should().Contain("connect <session-id>");
        help.Should().Contain("disconnect <session-id>");
    }
    /// <summary>
    /// Verifies Help Level15 Includes Admin Disconnect.
    /// </summary>

    [Fact]
    public void Help_Level15_IncludesAdminDisconnect()
    {
        var help = CliCommandPolicy.BuildHelpText(PrivilegeLevel.FromInt(15));
        help.Should().Contain("disconnect <session-id>");
        help.Should().Contain("show history user <username>");
    }
    /// <summary>
    /// Verifies Help Lists Commands Alphabetically By Synopsis.
    /// </summary>

    [Fact]
    public void Help_ListsCommandsAlphabeticallyBySynopsis()
    {
        var help = CliCommandPolicy.BuildHelpText(PrivilegeLevel.FromInt(15));
        var synopses = help.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Where(l => l.StartsWith("  ", StringComparison.Ordinal) && !l.StartsWith("   ", StringComparison.Ordinal))
            .Select(l => l.TrimStart().Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries)[0])
            .ToList();

        // First token of each primary help line should rise alphabetically.
        synopses.Should().Equal(synopses.OrderBy(s => s, CliDiscoveryOrdering.Comparer));
        synopses.First().Should().Be("attach");
    }
    /// <summary>
    /// Verifies Empty Tiers Inherit By Numeric Value.
    /// </summary>

    [Fact]
    public void EmptyTiers_InheritByNumericValue()
    {
        // Levels without unique commands still use cumulative numeric comparison.
        CliCommandPolicy.IsAllowed(PrivilegeLevel.FromInt(2), CliCommandId.ShowSessions).Should().BeTrue();
        CliCommandPolicy.IsAllowed(PrivilegeLevel.FromInt(2), CliCommandId.ShowSession).Should().BeTrue();
        CliCommandPolicy.IsAllowed(PrivilegeLevel.FromInt(1), CliCommandId.ShowHistory).Should().BeTrue();
        CliCommandPolicy.IsAllowed(PrivilegeLevel.FromInt(4), CliCommandId.ShowHistorySession).Should().BeFalse();
        CliCommandPolicy.IsAllowed(PrivilegeLevel.FromInt(6), CliCommandId.ShowHistorySession).Should().BeTrue();
        CliCommandPolicy.IsAllowed(PrivilegeLevel.FromInt(6), CliCommandId.Connect).Should().BeTrue();
        CliCommandPolicy.IsAllowed(PrivilegeLevel.FromInt(6), CliCommandId.Attach).Should().BeTrue();
        CliCommandPolicy.IsAllowed(PrivilegeLevel.FromInt(6), CliCommandId.ShowSerial).Should().BeFalse();
        CliCommandPolicy.IsAllowed(PrivilegeLevel.FromInt(6), CliCommandId.Disconnect).Should().BeFalse();
        CliCommandPolicy.IsAllowed(PrivilegeLevel.FromInt(8), CliCommandId.Attach).Should().BeTrue();
        CliCommandPolicy.IsAllowed(PrivilegeLevel.FromInt(8), CliCommandId.Disconnect).Should().BeTrue();
        CliCommandPolicy.IsAllowed(PrivilegeLevel.FromInt(10), CliCommandId.ShowHistoryUser).Should().BeFalse();
        CliCommandPolicy.IsAllowed(PrivilegeLevel.FromInt(14), CliCommandId.ShowHistoryUser).Should().BeTrue();
        CliCommandPolicy.IsAllowed(PrivilegeLevel.FromInt(14), CliCommandId.Disconnect).Should().BeTrue();
    }
}
