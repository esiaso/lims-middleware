namespace Lims.Contracts;

/// <summary>Well-known values for inbox_message.source and kind.</summary>
public static class MessageSources
{
    public const string Instrument = "instrument";
    public const string Hmis = "hmis";
}

public static class InboxKinds
{
    public const string AstmMessage = "astm.message";
    public const string Hl7Message = "hl7.message";
    public const string HmisOrder = "hmis.order";
    public const string HmisOrderEvent = "hmis.order.event";
}

public static class OutboxKinds
{
    public const string HmisResult = "hmis.result";
    public const string HmisAmendment = "hmis.amendment";
}
