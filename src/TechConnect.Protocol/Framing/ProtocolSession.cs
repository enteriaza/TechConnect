// -----------------------------------------------------------------------------
// Project: TechConnect.Protocol
// File:    ProtocolSession.cs
//
// Purpose:
// Pairs a frame reader and writer over a shared unowned stream and offers
// convenience helpers for serial-data and typed control-message writes.
//
// -----------------------------------------------------------------------------

using TechConnect.Protocol.Messages;
using TechConnect.Protocol.Serialization;

namespace TechConnect.Protocol.Framing;

/// <summary>
/// Convenience wrapper pairing a <see cref="FrameReader"/> and <see cref="FrameWriter"/>
/// over the same unowned stream.
/// </summary>
/// <remarks>
/// Typically wraps an authenticated <c>SslStream</c>. The session does not dispose
/// the stream. <see cref="DisposeAsync"/> disposes only the writer (releasing its
/// write gate). Concurrent writes are serialized by the writer; concurrent reads
/// on <see cref="Reader"/> remain unsupported.
/// </remarks>
public sealed class ProtocolSession : IAsyncDisposable
{
    /// <summary>
    /// Initializes a new session over the specified stream.
    /// </summary>
    /// <param name="stream">Shared byte stream for reading and writing frames; not owned.</param>
    /// <param name="limits">
    /// Optional payload limits shared by the reader and writer. When
    /// <see langword="null"/>, <see cref="ProtocolLimits.Default"/> is used.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    public ProtocolSession(Stream stream, ProtocolLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var effectiveLimits = limits ?? ProtocolLimits.Default;
        Reader = new FrameReader(stream, effectiveLimits);
        Writer = new FrameWriter(stream, effectiveLimits);
        Limits = effectiveLimits;
    }

    /// <summary>
    /// Gets the frame reader bound to the session stream.
    /// </summary>
    public FrameReader Reader { get; }

    /// <summary>
    /// Gets the frame writer bound to the session stream.
    /// </summary>
    public FrameWriter Writer { get; }

    /// <summary>
    /// Gets the effective payload limits used by both reader and writer.
    /// </summary>
    public ProtocolLimits Limits { get; }

    /// <summary>
    /// Writes a <see cref="MessageType.SerialData"/> frame with the given opaque octets.
    /// </summary>
    /// <param name="data">Raw serial payload; not interpreted as text or JSON.</param>
    /// <param name="cancellationToken">Token that can cancel the write.</param>
    /// <returns>A task that completes when the frame has been written.</returns>
    public Task WriteSerialDataAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
        Writer.WriteAsync(MessageType.SerialData, data, cancellationToken);

    /// <summary>
    /// Serializes a typed control message to UTF-8 JSON and writes it as a framed control payload.
    /// </summary>
    /// <param name="messageType">
    /// Control message type. Must not be <see cref="MessageType.SerialData"/>.
    /// </param>
    /// <param name="message">
    /// DTO instance matching <paramref name="messageType"/>; validated and serialized
    /// by <see cref="ControlMessageSerializer.Serialize"/>.
    /// </param>
    /// <param name="cancellationToken">Token that can cancel the write.</param>
    /// <returns>A task that completes when the frame has been written.</returns>
    /// <exception cref="ProtocolInvalidPayloadException">
    /// <paramref name="messageType"/> is <see cref="MessageType.SerialData"/>, or the
    /// message fails control validation / is the wrong CLR type for the message type.
    /// </exception>
    public Task WriteControlAsync(MessageType messageType, object message, CancellationToken cancellationToken = default)
    {
        if (messageType == MessageType.SerialData)
        {
            throw new ProtocolInvalidPayloadException("Use WriteSerialDataAsync for SerialData frames.");
        }

        var payload = ControlMessageSerializer.Serialize(messageType, message);
        return Writer.WriteAsync(messageType, payload, cancellationToken);
    }

    /// <summary>
    /// Writes a <see cref="MessageType.Hello"/> control frame for the given registration message.
    /// </summary>
    /// <param name="message">Client hello DTO to serialize and send.</param>
    /// <param name="cancellationToken">Token that can cancel the write.</param>
    /// <returns>A task that completes when the frame has been written.</returns>
    public Task WriteHelloAsync(HelloMessage message, CancellationToken cancellationToken = default) =>
        WriteControlAsync(MessageType.Hello, message, cancellationToken);

    /// <summary>
    /// Writes a <see cref="MessageType.HelloResult"/> control frame for the given result message.
    /// </summary>
    /// <param name="message">Hello result DTO to serialize and send.</param>
    /// <param name="cancellationToken">Token that can cancel the write.</param>
    /// <returns>A task that completes when the frame has been written.</returns>
    public Task WriteHelloResultAsync(HelloResultMessage message, CancellationToken cancellationToken = default) =>
        WriteControlAsync(MessageType.HelloResult, message, cancellationToken);

    /// <summary>
    /// Writes a <see cref="MessageType.OperatorStatus"/> control frame for the given status message.
    /// </summary>
    /// <param name="message">Operator status DTO to serialize and send.</param>
    /// <param name="cancellationToken">Token that can cancel the write.</param>
    /// <returns>A task that completes when the frame has been written.</returns>
    public Task WriteOperatorStatusAsync(OperatorStatusMessage message, CancellationToken cancellationToken = default) =>
        WriteControlAsync(MessageType.OperatorStatus, message, cancellationToken);

    /// <summary>
    /// Disposes the session writer without disposing the underlying stream.
    /// </summary>
    /// <returns>A value task that completes when the writer has been disposed.</returns>
    public ValueTask DisposeAsync() => Writer.DisposeAsync();
}
