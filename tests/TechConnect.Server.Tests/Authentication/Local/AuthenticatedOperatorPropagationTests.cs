// -----------------------------------------------------------------------------
// Project: TechConnect.Server.Tests
// File:    AuthenticatedOperatorPropagationTests.cs
//
// Purpose:
// Regression tests and helpers covering AuthenticatedOperatorPropagation behaviour.
// Fixture: AuthenticatedOperatorPropagationTests.
//
// -----------------------------------------------------------------------------

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TechConnect.Server.Attachment;
using TechConnect.Server.Authentication.Local;
using TechConnect.Server.Authentication.Models;
using TechConnect.Server.Connector;
using TechConnect.Server.Sessions;
using TechConnect.Server.Ssh.Cli;
using TechConnect.Server.Tests.Support;

namespace TechConnect.Server.Tests.Authentication.Local;
/// <summary>
/// xUnit test fixture for AuthenticatedOperatorPropagation.
/// </summary>

public sealed class AuthenticatedOperatorPropagationTests
{
    /// <summary>
    /// Field <c>T0</c> used by this implementation.
    /// </summary>
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-11-18T12:00:00Z");
    /// <summary>
    /// Verifies Claims Round Trip Preserves Identity.
    /// </summary>

    [Fact]
    public void ClaimsRoundTrip_PreservesIdentity()
    {
        var original = TestAuthenticatedOperator.Create(
            username: "Admin",
            privilege: PrivilegeLevel.OperatorBoundary,
            method: AuthenticationMethodKind.Password,
            operatorSessionId: "abc123session",
            localAccountId: "admin",
            publicKeyFingerprint: null);

        var principal = AuthenticatedOperatorClaims.CreatePrincipal(original);
        AuthenticatedOperatorClaims.TryCreateOperator(principal, out var restored).Should().BeTrue();
        restored.Should().BeEquivalentTo(original);
    }
    /// <summary>
    /// Verifies Try Attach Stores Same Authenticated Operator.
    /// </summary>

    [Fact]
    public void TryAttach_StoresSameAuthenticatedOperator()
    {
        var registry = new ConnectorSessionRegistry();
        var session = registry.TryRegister("ID", "c1", null, "host", null, "COM1", null, T0).Session!;
        var manager = new OperatorAttachmentManager(registry, TimeProvider.System);
        var identity = TestAuthenticatedOperator.Create("ops", PrivilegeLevel.AdministratorBoundary);

        var attach = manager.TryAttach(identity.OperatorSessionId, session.SessionId, "c1", identity);
        attach.Succeeded.Should().BeTrue();
        attach.Attachment!.Operator.Should().BeSameAs(identity);
        attach.Attachment.OperatorSessionId.Should().Be(identity.OperatorSessionId);
    }
    /// <summary>
    /// Verifies Cli Session Uses Authenticated Operator Without Substitution.
    /// </summary>

    [Fact]
    public void CliSession_UsesAuthenticatedOperator_WithoutSubstitution()
    {
        var identity = TestAuthenticatedOperator.Create("cli-user", PrivilegeLevel.ReadOnlyBoundary);
        var (dispatcher, registry, attachments, serialCache, pendingBaud, authorization, _, tracker, disconnect, enable) =
            CliTestFactory.CreateDispatcher();

        using var ms = new MemoryStream();
        var session = new TechConnectCliSession(
            ms,
            dispatcher,
            attachments,
            registry,
            tracker,
            serialCache,
            pendingBaud,
            authorization,
            disconnect,
            NullLogger.Instance,
            identity,
            enable);

        session.AuthenticatedOperator.Should().BeSameAs(identity);
        session.OperatorSessionId.Should().Be(identity.OperatorSessionId);
        session.AuthenticatedOperator.Username.Should().Be("cli-user");
        session.AuthenticatedOperator.Privilege.Should().Be(PrivilegeLevel.ReadOnlyBoundary);
        session.ActivePrivilege.Should().Be(PrivilegeLevel.ReadOnlyBoundary);
    }
    /// <summary>
    /// Verifies Attachment Disconnect Cleanup Remains Correct With Identity.
    /// </summary>

    [Fact]
    public void Attachment_DisconnectCleanup_RemainsCorrect_WithIdentity()
    {
        var registry = new ConnectorSessionRegistry();
        var session = registry.TryRegister("ID", "c1", null, "host", null, "COM1", null, T0).Session!;
        var manager = new OperatorAttachmentManager(registry, TimeProvider.System);
        var identity = TestAuthenticatedOperator.Create("ops");

        var attach = manager.TryAttach(identity.OperatorSessionId, session.SessionId, "c1", identity);
        attach.Succeeded.Should().BeTrue();
        attach.Attachment!.Operator.Should().NotBeNull();

        manager.TryDetachByOperatorSessionId(
                identity.OperatorSessionId,
                AttachmentDetachReason.OperatorRequest,
                out _)
            .Should().BeTrue();
        manager.Count.Should().Be(0);
    }
}
