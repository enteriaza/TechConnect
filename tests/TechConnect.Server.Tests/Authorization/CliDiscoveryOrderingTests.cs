// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    CliDiscoveryOrderingTests.cs
//
// Purpose:
// Regression tests and helpers covering CliDiscoveryOrdering behaviour.
// Fixture: CliDiscoveryOrderingTests.
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using TechConnect.Server.Authorization;
using TechConnect.Server.Ssh.Cli;

namespace TechConnect.Server.Tests.Authorization;
/// <summary>
/// xUnit test fixture for CliDiscoveryOrdering.
/// </summary>

public sealed class CliDiscoveryOrderingTests
{
    /// <summary>
    /// Verifies Comparer Is Case Insensitive With Ordinal Tie Break.
    /// </summary>
    [Fact]
    public void Comparer_IsCaseInsensitive_WithOrdinalTieBreak()
    {
        var items = new[] { "banana", "Apple", "apple", "Zebra", "apricot" };
        var sorted = CliDiscoveryOrdering.Sort(items);
        // Ignore-case primary (p < r → Apple/apple before apricot); ordinal tie-break Apple < apple.
        sorted.Should().Equal("Apple", "apple", "apricot", "banana", "Zebra");
    }
    /// <summary>
    /// Verifies Sort Entries Orders By Token Preserves Descriptions.
    /// </summary>

    [Fact]
    public void Sort_Entries_OrdersByToken_PreservesDescriptions()
    {
        var entries = new (string Token, string Description)[]
        {
            ("zebra", "z"),
            ("Alpha", "a1"),
            ("alpha", "a2"),
            ("beta", "b")
        };
        var sorted = CliDiscoveryOrdering.Sort(entries);
        sorted.Select(e => e.Token).Should().Equal("Alpha", "alpha", "beta", "zebra");
        sorted.Select(e => e.Description).Should().Equal("a1", "a2", "b", "z");
    }
    /// <summary>
    /// Verifies Format Help Listing Sorts Unsorted Input.
    /// </summary>

    [Fact]
    public void FormatHelpListing_SortsUnsortedInput()
    {
        var entries = new (string Token, string Description)[]
        {
            ("stopbits", "Serial stop bits"),
            ("baud", "Serial baud rate"),
            ("parity", "Serial parity"),
            ("databits", "Serial data bits")
        };
        var text = CliDiscoveryFormatter.FormatHelpListing(entries, 80);
        var lines = text.Split(["\r\n", "\n"], StringSplitOptions.None);
        lines.Select(l => l.TrimStart().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0])
            .Should()
            .Equal("baud", "databits", "parity", "stopbits");
    }
    /// <summary>
    /// Verifies Format Candidate Columns Sorts Unsorted Input.
    /// </summary>

    [Fact]
    public void FormatCandidateColumns_SortsUnsortedInput()
    {
        var text = CliDiscoveryFormatter.FormatCandidateColumns(
            ["sessions", "history", "serial", "session"],
            80);
        text.Should().MatchRegex(@"history.*serial.*session.*sessions");
    }
}
