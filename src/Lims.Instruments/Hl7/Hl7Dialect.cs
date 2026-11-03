using Lims.Instruments.Generic;

namespace Lims.Instruments.Hl7;

/// <summary>What the HL7 link does with a received message.</summary>
public enum Hl7Inbound
{
    /// <summary>Results: store, then AA.</summary>
    Result,
    /// <summary>Order query: store, then answer with the worklist reply.</summary>
    Query,
    /// <summary>The analyzer's acknowledgement of a message we sent: not stored, not answered.</summary>
    Acknowledgement,
    /// <summary>Known message type but a required segment is missing: AE 100.</summary>
    Malformed,
    /// <summary>Known message type, unsupported trigger event: AR 201.</summary>
    UnsupportedEvent,
    /// <summary>AR 200.</summary>
    UnsupportedType,
}

/// <summary>The analyzer-specific parts of an HL7 link: how messages are classified and how replies are written.</summary>
public interface IHl7Dialect
{
    Hl7Inbound Classify(Hl7Message msg);
    string Ack(string ackCode, Hl7Message? received, DateTimeOffset now, int? errorCode = null, string? errorText = null);
    /// <summary>Worklist reply when no order is known or host query is off.</summary>
    string EmptyQueryReply(Hl7Message received, DateTimeOffset now);
}

/// <summary>Mindray BC-5000 "3107 HL7 Communication Protocol" V2.0.</summary>
public sealed class MindrayBc5000Dialect : IHl7Dialect
{
    public static readonly MindrayBc5000Dialect Instance = new();

    public Hl7Inbound Classify(Hl7Message msg) => (msg.MessageType, msg.TriggerEvent) switch
    {
        ("ORU", "R01") => msg.First("OBR") is null ? Hl7Inbound.Malformed : Hl7Inbound.Result,
        ("ORM", "O01") => msg.First("ORC") is null ? Hl7Inbound.Malformed : Hl7Inbound.Query,
        ("ORM", _) or ("ORU", _) => Hl7Inbound.UnsupportedEvent,
        _ => Hl7Inbound.UnsupportedType,
    };

    public string Ack(string ackCode, Hl7Message? received, DateTimeOffset now, int? errorCode = null, string? errorText = null) =>
        MindrayBc5000Replies.Ack(ackCode, received?.ControlId ?? "", received?.ProcessingId ?? "P", now, errorCode, errorText);

    public string EmptyQueryReply(Hl7Message received, DateTimeOffset now) => MindrayBc5000Replies.WorklistReply(received.ControlId, null, null, now);
}

/// <summary>An analyzer registered from the screens, with its configured message types.</summary>
public sealed class GenericHl7Dialect(GenericAdapterProfile profile) : IHl7Dialect
{
    public Hl7Inbound Classify(Hl7Message msg)
    {
        var type = $"{msg.MessageType}^{msg.TriggerEvent}";
        if (msg.MessageType == "ACK") return Hl7Inbound.Acknowledgement;
        if (type.Equals(profile.Hl7QueryMessage, StringComparison.OrdinalIgnoreCase)) return Hl7Inbound.Query;
        if (msg.First("OBX") is not null) return msg.First("OBR") is null ? Hl7Inbound.Malformed : Hl7Inbound.Result;
        return msg.MessageType is "ORU" or "OUL" ? Hl7Inbound.Malformed : Hl7Inbound.UnsupportedType;
    }

    public string Ack(string ackCode, Hl7Message? received, DateTimeOffset now, int? errorCode = null, string? errorText = null) =>
        GenericHl7Messages.Ack(profile, ackCode, received?.ControlId ?? "", received?.ProcessingId ?? "P", received?.TriggerEvent, now, errorCode, errorText);

    public string EmptyQueryReply(Hl7Message received, DateTimeOffset now) => GenericHl7Messages.QueryReply(profile, received.ControlId, null, null, now);
}
