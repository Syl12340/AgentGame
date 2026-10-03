namespace AgentGame.Protocol;

/// <summary>
/// Thrown when a protocol message violates a frozen agent/1, observer/1, replay/1
/// or scenario/1 contract (unknown/missing/extra/duplicate fields, wrong type or
/// enum value, null, invalid UTF-8, over-length line, over-deep JSON, or extra JSON).
/// The Protocol library never computes game rules; failures here are transport/DTD errors.
/// </summary>
public sealed class ProtocolException : Exception
{
    public ProtocolException(string message) : base(message) { }
    public ProtocolException(string message, Exception inner) : base(message, inner) { }
}