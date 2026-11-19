// -----------------------------------------------------------------------------
// Project: TechConnect.Server
// File:    SshHostKeyProvider.cs
//
// Purpose:
// Lazily loads or generates the SSH host key from appsettings / bound options,
// exposing fingerprint and source for listener startup logging.
//
// -----------------------------------------------------------------------------

using Microsoft.DevTunnels.Ssh.Algorithms;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TechConnect.Server.Configuration;

namespace TechConnect.Server.Ssh.Authentication;

/// <summary>
/// Lazy, thread-safe provider of the SSH host key pair used by the SSH listener.
/// </summary>
/// <remarks>
/// Resolution order: effective JSON key via <see cref="AppsettingsHostKeyStore"/>,
/// then bound <see cref="SshServerOptions.HostPrivateKey"/> (for in-memory test overrides),
/// then generate + persist. Never logs private key material — only algorithm and fingerprint.
/// </remarks>
internal sealed class SshHostKeyProvider
{
    /// <summary>
    /// Bound TechConnect options (fallback when no JSON file key exists).
    /// </summary>
    private readonly IOptions<TechConnectServerOptions> _options;

    /// <summary>
    /// File-backed host-key resolve/persist store.
    /// </summary>
    private readonly AppsettingsHostKeyStore _hostKeyStore;

    /// <summary>
    /// Logger for load/generate events (fingerprint only).
    /// </summary>
    private readonly ILogger<SshHostKeyProvider> _logger;

    /// <summary>
    /// Gate serializing first-time load/generation.
    /// </summary>
    private readonly object _gate = new();

    /// <summary>
    /// Cached host key after first successful load.
    /// </summary>
    private IKeyPair? _hostKey;

    /// <summary>
    /// Cached SHA256 fingerprint string.
    /// </summary>
    private string? _fingerprint;

    /// <summary>
    /// Human-readable source path or <c>configuration</c> label.
    /// </summary>
    private string? _source;

    /// <summary>
    /// Initializes the host key provider.
    /// </summary>
    /// <param name="options">Bound TechConnect server options.</param>
    /// <param name="hostKeyStore">Appsettings-backed key store.</param>
    /// <param name="logger">Logger instance.</param>
    /// <exception cref="ArgumentNullException">Any required dependency is <see langword="null"/>.</exception>
    public SshHostKeyProvider(
        IOptions<TechConnectServerOptions> options,
        AppsettingsHostKeyStore hostKeyStore,
        ILogger<SshHostKeyProvider> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _hostKeyStore = hostKeyStore ?? throw new ArgumentNullException(nameof(hostKeyStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// SHA256 fingerprint of the loaded host public key.
    /// </summary>
    /// <value>Loads the key on first access.</value>
    public string Fingerprint
    {
        get
        {
            EnsureLoaded();
            return _fingerprint!;
        }
    }

    /// <summary>
    /// Source description for the loaded key (file path or <c>configuration</c>).
    /// </summary>
    /// <value>Loads the key on first access.</value>
    public string Source
    {
        get
        {
            EnsureLoaded();
            return _source!;
        }
    }

    /// <summary>
    /// Returns the loaded host key pair (loads/generates on first call).
    /// </summary>
    /// <returns>Host <see cref="IKeyPair"/> including private key.</returns>
    public IKeyPair GetHostKey()
    {
        EnsureLoaded();
        return _hostKey!;
    }

    /// <summary>
    /// Ensures the host key, fingerprint, and source are loaded exactly once.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Generation/persist raced and no effective key could be re-read, or import failed.
    /// </exception>
    private void EnsureLoaded()
    {
        if (_hostKey is not null)
        {
            return;
        }

        lock (_gate)
        {
            if (_hostKey is not null)
            {
                return;
            }

            // Prefer file-based effective resolution so an empty environment-specific
            // HostPrivateKey cannot shadow a valid base key (and so we know the source file).
            var effective = _hostKeyStore.TryResolveEffectiveHostPrivateKey();
            if (effective is not null)
            {
                var key = SshKeyMaterial.ImportHostPrivateKey(effective.PrivateKeyPem);
                SetLoaded(key, effective.SourceFilePath);
                return;
            }

            // Fall back to bound options (e.g. in-memory test overrides) when no JSON file key exists.
            var configured = _options.Value.Ssh.HostPrivateKey;
            if (!string.IsNullOrWhiteSpace(configured))
            {
                var key = SshKeyMaterial.ImportHostPrivateKey(configured);
                SetLoaded(key, "configuration");
                return;
            }

            var generated = SshKeyMaterial.GenerateHostKeyPair();
            var pem = SshKeyMaterial.ExportPrivateKeyPem(generated);

            try
            {
                _hostKeyStore.PersistGeneratedHostPrivateKey(pem);
            }
            catch (InvalidOperationException)
            {
                var raced = _hostKeyStore.TryResolveEffectiveHostPrivateKey()
                    ?? ( _hostKeyStore.TryReadPersistedHostPrivateKey() is { } p
                        ? new EffectiveHostPrivateKey(p, _hostKeyStore.PersistenceFilePath)
                        : null);

                if (raced is null)
                {
                    throw;
                }

                var key = SshKeyMaterial.ImportHostPrivateKey(raced.PrivateKeyPem);
                SetLoaded(key, raced.SourceFilePath);
                return;
            }

            var persisted = _hostKeyStore.TryReadPersistedHostPrivateKey()
                ?? throw new InvalidOperationException(
                    "SSH host key was persisted but could not be re-read from configuration.");
            var loaded = SshKeyMaterial.ImportHostPrivateKey(persisted);
            SetLoaded(loaded, _hostKeyStore.PersistenceFilePath);
            _logger.LogInformation(
                "Generated and persisted a new SSH host key (algorithm {Algorithm}, fingerprint {Fingerprint})",
                loaded.KeyAlgorithmName,
                _fingerprint);
        }
    }

    /// <summary>
    /// Stores the loaded key, fingerprint, and source and logs the fingerprint.
    /// </summary>
    /// <param name="key">Imported or generated host key.</param>
    /// <param name="source">Source path or label.</param>
    private void SetLoaded(IKeyPair key, string source)
    {
        _hostKey = key;
        _fingerprint = SshKeyMaterial.ComputeFingerprint(key);
        _source = source;
        _logger.LogInformation(
            "Loaded SSH host key from {Source} (algorithm {Algorithm}, fingerprint {Fingerprint})",
            source,
            key.KeyAlgorithmName,
            _fingerprint);
    }
}
