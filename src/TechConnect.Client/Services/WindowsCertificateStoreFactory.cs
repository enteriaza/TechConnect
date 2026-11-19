// -----------------------------------------------------------------------------
// Project: TechConnect.Client
// File:    WindowsCertificateStoreFactory.cs
//
// Purpose:
// Creates disposable Windows certificate-store adapters for client cert lookup.
//
// -----------------------------------------------------------------------------

using TechConnect.Client.Abstractions;

namespace TechConnect.Client.Services;

/// <summary>
/// Default <see cref="ICertificateStoreFactory"/> producing <see cref="WindowsCertificateStore"/>.
/// </summary>
/// <remarks>
/// Each call returns a new unopened store adapter. The caller must open and dispose it.
/// The factory itself is thread-safe and holds no certificate material.
/// </remarks>
internal sealed class WindowsCertificateStoreFactory : ICertificateStoreFactory
{
    /// <inheritdoc />
    public ICertificateStore Create() => new WindowsCertificateStore();
}
