namespace DataUsage.Core.Srum;

/// <summary>
/// One SRUM network-usage row, in the shape the database stores.
/// </summary>
/// <remarks>
/// Strings where a number would look natural are deliberate: LUIDs exceed
/// 2^53, and the other ids are keys, not quantities. Keeping them as text also
/// keeps them byte-identical to the rows imported from the web dashboard's
/// database, which share this dedup key.
/// </remarks>
public sealed record UsageRow(
    string TimestampUtc,
    string LocalDate,
    int LocalHour,
    long AppId,
    bool IsAggregate,
    string AppIdentity,
    string AppKind,
    string UserId,
    string Sid,
    string InterfaceLuid,
    string InterfaceType,
    string L2ProfileId,
    long BytesSent,
    long BytesReceived);
