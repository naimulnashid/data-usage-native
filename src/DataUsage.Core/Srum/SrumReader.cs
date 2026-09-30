using System.Globalization;
using Microsoft.Isam.Esent.Interop;

namespace DataUsage.Core.Srum;

/// <summary>
/// Reads the network-usage table out of a SRUM database through Windows' own
/// ESE engine (esent.dll) - the same engine SrumECmd drives. No part of the
/// file format is parsed here; what this class adds is what SrumECmd adds on
/// top of the engine: resolving ids through SruDbIdMapTable, naming the
/// interface type, and the timestamp.
/// </summary>
/// <remarks>
/// <para>The database must be CLEAN. A VSS copy of a live ESE database is
/// always in Dirty Shutdown state, and attaching one throws
/// <c>EsentDatabaseDirtyShutdownException</c> - so <see cref="SrumRecovery"/>
/// runs first, always. This reader never replays logs itself: its instance has
/// recovery off and an empty private log directory, so it can only ever read.</para>
/// <para>Point it at a COPY in scratch, never the live SRUDB.dat.</para>
/// </remarks>
public static class SrumReader
{
    /// <summary>Network Data Usage Monitor.</summary>
    public const string NetworkTable = "{973F5D5C-1D90-4944-BE8E-24B94231A174}";

    private const string IdMapTable = "SruDbIdMapTable";

    /// <summary>IdMap's IdType for a user: its blob is a binary SID, not text.</summary>
    private const byte IdTypeSid = 3;

    private static readonly Lock EngineLock = new();

    /// <summary>Every network-usage row in the database, in table order.</summary>
    public static List<UsageRow> ReadNetworkUsage(string databasePath, TimeZoneInfo? zone = null)
    {
        var path = Path.GetFullPath(databasePath);
        if (!File.Exists(path)) throw new FileNotFoundException("SRUM database not found", path);

        // The page size is a process-wide engine setting that must match the
        // file (SRUM uses 4 KB here; ESE's default is 8 KB). One reader at a
        // time, so two callers cannot race on it.
        lock (EngineLock)
        {
            Api.JetGetDatabaseFileInfo(path, out int pageSize, JET_DbInfo.PageSize);
            SystemParameters.DatabasePageSize = pageSize;

            var work = Path.Combine(Path.GetTempPath(), "DataUsageNative-ese-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            try
            {
                using var instance = new Instance("DataUsageNative-" + Guid.NewGuid().ToString("N"));
                instance.Parameters.Recovery = false;
                instance.Parameters.CircularLog = true;
                instance.Parameters.NoInformationEvent = true;
                instance.Parameters.CreatePathIfNotExist = true;
                instance.Parameters.LogFileDirectory = work;
                instance.Parameters.SystemDirectory = work;
                instance.Parameters.TempDirectory = work;
                instance.Init();

                using var session = new Session(instance);
                Api.JetAttachDatabase(session, path, AttachDatabaseGrbit.ReadOnly);
                Api.JetOpenDatabase(session, path, null, out var dbid, OpenDatabaseGrbit.ReadOnly);
                try
                {
                    var ids = ReadIdMap(session, dbid);
                    return ReadRows(session, dbid, ids, zone ?? TimeZoneInfo.Local);
                }
                finally
                {
                    Api.JetCloseDatabase(session, dbid, CloseDatabaseGrbit.None);
                    Api.JetDetachDatabase(session, path);
                }
            }
            finally
            {
                try { Directory.Delete(work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static Dictionary<long, (byte Type, byte[]? Blob)> ReadIdMap(Session session, JET_DBID dbid)
    {
        var map = new Dictionary<long, (byte, byte[]?)>();
        using var table = new Table(session, dbid, IdMapTable, OpenTableGrbit.ReadOnly);
        var cols = Api.GetColumnDictionary(session, table);
        var colIndex = cols["IdIndex"];
        var colType = cols["IdType"];
        var colBlob = cols["IdBlob"];

        Api.MoveBeforeFirst(session, table);
        while (Api.TryMoveNext(session, table))
        {
            var index = Api.RetrieveColumnAsInt32(session, table, colIndex);
            if (index is null) continue;
            var type = Api.RetrieveColumnAsByte(session, table, colType) ?? 0;
            map[index.Value] = (type, Api.RetrieveColumn(session, table, colBlob));
        }
        return map;
    }

    private static List<UsageRow> ReadRows(Session session, JET_DBID dbid, Dictionary<long, (byte Type, byte[]? Blob)> ids, TimeZoneInfo zone)
    {
        var rows = new List<UsageRow>();
        using var table = new Table(session, dbid, NetworkTable, OpenTableGrbit.ReadOnly);
        var cols = Api.GetColumnDictionary(session, table);
        var colTime = cols["TimeStamp"];
        var colApp = cols["AppId"];
        var colUser = cols["UserId"];
        var colLuid = cols["InterfaceLuid"];
        var colProfile = cols["L2ProfileId"];
        var colSent = cols["BytesSent"];
        var colRecvd = cols["BytesRecvd"];

        // Repeated strings (paths, SIDs) are shared, not re-decoded per row.
        var appText = new Dictionary<long, string>();
        var sidText = new Dictionary<long, string>();

        Api.MoveBeforeFirst(session, table);
        while (Api.TryMoveNext(session, table))
        {
            var stamp = Api.RetrieveColumnAsDateTime(session, table, colTime);
            if (stamp is null) continue;
            // Whole seconds, as SrumECmd printed them; the OLE date's float can
            // carry a stray millisecond that would split the dedup key.
            var utc = new DateTime(stamp.Value.Ticks - stamp.Value.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);

            long appId = Api.RetrieveColumnAsInt32(session, table, colApp) ?? 0;
            long userId = Api.RetrieveColumnAsInt32(session, table, colUser) ?? 0;
            long luid = Api.RetrieveColumnAsInt64(session, table, colLuid) ?? 0;
            long profile = Api.RetrieveColumnAsInt32(session, table, colProfile) ?? 0;

            if (!appText.TryGetValue(appId, out var identity))
            {
                // The aggregate row carries no identity, and an id with no map
                // entry has none to give.
                identity = appId != SrumFields.AggregateAppId && ids.TryGetValue(appId, out var app) && app.Type != IdTypeSid
                    ? SrumFields.DecodeText(app.Blob).Trim()
                    : "";
                appText[appId] = identity;
            }
            if (!sidText.TryGetValue(userId, out var sid))
            {
                sid = ids.TryGetValue(userId, out var user) && user.Type == IdTypeSid ? SrumFields.DecodeSid(user.Blob) : "";
                sidText[userId] = sid;
            }

            var (date, hour) = SrumFields.LocalBuckets(utc, zone);
            rows.Add(new UsageRow(
                SrumFields.IsoUtc(utc),
                date,
                hour,
                appId,
                appId == SrumFields.AggregateAppId,
                identity,
                SrumFields.ClassifyApp(appId, identity),
                userId.ToString(CultureInfo.InvariantCulture),
                sid,
                luid.ToString(CultureInfo.InvariantCulture),
                SrumFields.InterfaceType(luid),
                profile.ToString(CultureInfo.InvariantCulture),
                Api.RetrieveColumnAsInt64(session, table, colSent) ?? 0,
                Api.RetrieveColumnAsInt64(session, table, colRecvd) ?? 0));
        }
        return rows;
    }
}
