// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    SshHostKeyOptionsSetup.cs
//
// Purpose:
// Post-configures TechConnectServerOptions so an empty environment HostPrivateKey
// override cannot wipe a valid base key resolved from appsettings JSON files.
//
// -----------------------------------------------------------------------------

using Microsoft.Extensions.Options;

namespace TechConnect.Server.Configuration;

/// <summary>
/// After binding, repairs an empty effective <c>HostPrivateKey</c> by resolving base/environment
/// JSON files so an empty environment override cannot wipe a valid base key.
/// </summary>
/// <remarks>
/// Registered as <see cref="IPostConfigureOptions{TOptions}"/> for
/// <see cref="TechConnectServerOptions"/>. Runs before options validation and before
/// <see cref="Ssh.Authentication.SshHostKeyProvider"/> loads the host key.
/// </remarks>
internal sealed class SshHostKeyOptionsSetup : IPostConfigureOptions<TechConnectServerOptions>
{
    /// <summary>
    /// File-backed host-key store used to resolve the effective PEM when bound options are empty.
    /// </summary>
    private readonly AppsettingsHostKeyStore _hostKeyStore;

    /// <summary>
    /// Initializes a new post-configure hook.
    /// </summary>
    /// <param name="hostKeyStore">Store that reads base and environment appsettings JSON.</param>
    /// <exception cref="ArgumentNullException"><paramref name="hostKeyStore"/> is <see langword="null"/>.</exception>
    public SshHostKeyOptionsSetup(AppsettingsHostKeyStore hostKeyStore)
    {
        _hostKeyStore = hostKeyStore ?? throw new ArgumentNullException(nameof(hostKeyStore));
    }

    /// <summary>
    /// Fills <see cref="SshServerOptions.HostPrivateKey"/> from the effective JSON key when the
    /// bound value is null/whitespace.
    /// </summary>
    /// <param name="name">Options name (unused; named options are not used).</param>
    /// <param name="options">Bound TechConnect server options to repair.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public void PostConfigure(string? name, TechConnectServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Ssh ??= new SshServerOptions();

        if (!string.IsNullOrWhiteSpace(options.Ssh.HostPrivateKey))
        {
            return;
        }

        var effective = _hostKeyStore.TryResolveEffectiveHostPrivateKey();
        if (effective is not null)
        {
            options.Ssh.HostPrivateKey = effective.PrivateKeyPem;
        }
    }
}
