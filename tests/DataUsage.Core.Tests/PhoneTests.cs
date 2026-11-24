using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DataUsage.Core.Android;
using DataUsage.Core.Naming;
using DataUsage.Core.Query;
using Microsoft.Data.Sqlite;

namespace DataUsage.Core.Tests;

public class PhoneTests
{
    private const string Device = "00000000-test-phone";
    private static readonly long Ten = new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 0, 0, 0, TimeSpan.Zero);

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    private static string Upload(long rx, long tx, string network = "wifi", int offset = 360, string extra = "") => $$"""
        {"deviceId":"{{Device}}","label":"Test Phone","utcOffsetMinutes":{{offset}},
         "apps":[{"uid":10123,"package":"com.example.app","label":"Example","isSystem":false}],
         "buckets":[{"uid":10123,"start":{{Ten}},"network":"{{network}}","metered":false,"roaming":false,"rx":{{rx}},"tx":{{tx}}{{extra}}}]}
        """;

    private static PhonePayload Payload(long rx, long tx) => PhoneIngest.Validate(Json(Upload(rx, tx)), Now);

    private static (long Rx, long Tx) Stored(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT rx_bytes, tx_bytes FROM android_usage_records";
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
        var row = (r.GetInt64(0), r.GetInt64(1));
        Assert.False(r.Read());
        return row;
    }

    [Fact]
    public void ALaterLargerReadingOfABucketReplacesThePartialOne()
    {
        // A NetworkStats bucket is a running total: read mid-window, then again.
        using var t = new TestDb();
        var first = PhoneIngest.Ingest(t.Conn, Payload(100, 10));
        Assert.Equal(1, first.Written);
        var second = PhoneIngest.Ingest(t.Conn, Payload(250, 30));
        Assert.Equal(0, second.Written);
        Assert.Equal(1, second.Updated);
        Assert.Equal((250L, 30L), Stored(t.Conn));
    }

    [Fact]
    public void AStalePartialArrivingLateDoesNotShrinkABucket()
    {
        using var t = new TestDb();
        PhoneIngest.Ingest(t.Conn, Payload(250, 30));
        var late = PhoneIngest.Ingest(t.Conn, Payload(100, 10));
        Assert.Equal(0, late.Written);
        Assert.Equal(0, late.Updated);
        Assert.Equal((250L, 30L), Stored(t.Conn));
    }

    [Fact]
    public void TheSameUploadTwiceChangesNothing()
    {
        using var t = new TestDb();
        PhoneIngest.Ingest(t.Conn, Payload(250, 30));
        var again = PhoneIngest.Ingest(t.Conn, Payload(250, 30));
        Assert.Equal((0L, 0L), (again.Written, again.Updated));
    }

    [Fact]
    public void LocalDayAndHourComeFromThePhonesOffsetNotThisMachines()
    {
        // 10:00 UTC at +6:00 is 16:00; a bucket carrying its own offset uses it.
        Assert.Equal(("2026-11-20", 16), PhoneIngest.LocalParts(Ten, 360));
        Assert.Equal(("2026-11-19", 23), PhoneIngest.LocalParts(Ten, -660));
        using var t = new TestDb();
        PhoneIngest.Ingest(t.Conn, PhoneIngest.Validate(Json(Upload(1, 1, extra: ",\"utcOffsetMinutes\":-300")), Now));
        using var cmd = t.Conn.CreateCommand();
        cmd.CommandText = "SELECT local_date || ' ' || local_hour || ' ' || bucket_start_utc FROM android_usage_records";
        Assert.Equal("2026-11-20 5 2026-11-20T10:00:00.000Z", cmd.ExecuteScalar());
    }

    [Theory]
    [InlineData("""{"deviceId":"short","utcOffsetMinutes":0,"apps":[],"buckets":[]}""", "deviceId")]
    [InlineData("""{"deviceId":"00000000-test","utcOffsetMinutes":2000,"apps":[],"buckets":[]}""", "utcOffsetMinutes")]
    [InlineData("""{"deviceId":"00000000-test","utcOffsetMinutes":0,"apps":{},"buckets":[]}""", "arrays")]
    [InlineData("""{"deviceId":"00000000-test","utcOffsetMinutes":0,"apps":[{"uid":1.5,"package":"a"}],"buckets":[]}""", "app.uid")]
    [InlineData("""{"deviceId":"00000000-test","utcOffsetMinutes":0,"apps":[],"buckets":[{"uid":1,"start":1700000000000,"network":"vpn","rx":1,"tx":1}]}""", "network")]
    [InlineData("""{"deviceId":"00000000-test","utcOffsetMinutes":0,"apps":[],"buckets":[{"uid":1,"start":1700000000000,"network":"wifi","rx":-1,"tx":1}]}""", "rx")]
    [InlineData("""{"deviceId":"00000000-test","utcOffsetMinutes":0,"apps":[],"buckets":[{"uid":1,"start":4102444800000,"network":"wifi","rx":1,"tx":1}]}""", "future")]
    [InlineData("""{"deviceId":"00000000-test","utcOffsetMinutes":0,"model":7,"apps":[],"buckets":[]}""", "model")]
    public void AnUploadThatIsWrongIsTurnedAwayWithAReason(string json, string reason)
    {
        var ex = Assert.Throws<PhoneIngest.InvalidPayloadException>(() => PhoneIngest.Validate(Json(json), Now));
        Assert.Contains(reason, ex.Message);
    }

    // ---- The cloud inbox ----

    private sealed record Fixture(string KeyPkcs8, string PublicKey, string Fingerprint, string Token, string Bearer, string Envelope);

    private static Fixture Web() => JsonSerializer.Deserialize<Fixture>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "web-envelope.json")),
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    private static ECDiffieHellman KeyOf(Fixture f) => Inbox.KeyFromPem($"-----BEGIN PRIVATE KEY-----\n{f.KeyPkcs8}\n-----END PRIVATE KEY-----");

    [Fact]
    public void AnEnvelopeSealedByTheWebDashboardOpensHere()
    {
        var f = Web();
        using var key = KeyOf(f);
        Assert.Equal(f.PublicKey, Inbox.PublicKeyB64(key));
        Assert.Equal(f.Fingerprint, Inbox.Fingerprint(f.PublicKey));
        Assert.Equal(f.Bearer, Inbox.UploadBearer(f.Token));
        using var doc = Inbox.EnvelopeJson(Convert.FromBase64String(f.Envelope), key, f.Token);
        var payload = PhoneIngest.Validate(doc.RootElement, Now);
        Assert.Equal("00000000-fixture-device", payload.DeviceId);
        Assert.Equal(2, payload.Buckets.Count);
        Assert.Equal(300, payload.Buckets[1].UtcOffsetMinutes);
    }

    [Fact]
    public void AWrongTokenOrATamperedByteFailsAtTheTag()
    {
        var f = Web();
        using var key = KeyOf(f);
        var envelope = Convert.FromBase64String(f.Envelope);
        Assert.Throws<CryptographicException>(() => Inbox.Open(envelope, key, f.Token + "x"));
        envelope[^20] ^= 1;
        Assert.Throws<CryptographicException>(() => Inbox.Open(envelope, key, f.Token));
    }

    [Fact]
    public void SealAndOpenRoundTrip()
    {
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var plain = Encoding.UTF8.GetBytes("hello");
        var sealedBytes = Inbox.Seal(plain, Inbox.PublicKeyB64(key), "token");
        Assert.Equal(plain, Inbox.Open(sealedBytes, key, "token"));
    }

    /// <summary>The inbox's two calls, answered from memory; records what was acknowledged.</summary>
    private sealed class FakeInbox(string envelope) : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];
        public string? Ack { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            var path = request.RequestUri!.PathAndQuery;
            Calls.Add($"{request.Method} {path} {request.Headers.Authorization}");
            string body;
            if (path.Contains("/pending", StringComparison.Ordinal))
                body = path.Contains("after=0", StringComparison.Ordinal)
                    ? JsonSerializer.Serialize(new { ok = true, envelopes = new object[]
                    {
                        new { id = 7, received_at = "2026-11-20T18:30:00.123456+00:00", device_hint = "hint", envelope },
                        new { id = 8, received_at = "2026-11-20T19:00:00+00:00", device_hint = "hint-2", envelope = Convert.ToBase64String(new byte[120]) },
                    } })
                    : """{"ok":true,"envelopes":[]}""";
            else
            {
                Ack = await request.Content!.ReadAsStringAsync(cancel);
                body = """{"ok":true}""";
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task ADrainLandsWhatOpensMarksTheRestFailedAndAcknowledgesBoth()
    {
        var f = Web();
        using var t = new TestDb();
        var keyPath = Path.Combine(t.Dir, "inbox-key.pem");
        File.WriteAllText(keyPath, $"-----BEGIN PRIVATE KEY-----\n{f.KeyPkcs8}\n-----END PRIVATE KEY-----\n");
        File.WriteAllText(Path.Combine(t.Dir, "inbox-tokens.json"), JsonSerializer.Serialize(new { drainToken = "drain-token", ingestToken = f.Token }));
        var fake = new FakeInbox(f.Envelope);

        var r = await Inbox.DrainAsync(t.Conn, new InboxSettings { Url = "https://inbox.example/functions/v1/inbox/", KeyPath = keyPath }, handler: fake, cancel: TestContext.Current.CancellationToken);

        Assert.Equal((1, 1, 2L), (r.Landed, r.Failed, r.RowsWritten));
        Assert.All(fake.Calls, c => Assert.EndsWith("Bearer drain-token", c, StringComparison.Ordinal));
        Assert.StartsWith("GET /functions/v1/inbox/pending?limit=10&after=0", fake.Calls[0], StringComparison.Ordinal);
        using (var ack = JsonDocument.Parse(fake.Ack!))
        {
            Assert.Equal(7, ack.RootElement.GetProperty("done")[0].GetInt64());
            Assert.Equal(8, ack.RootElement.GetProperty("failed")[0].GetProperty("id").GetInt64());
        }
        Assert.StartsWith("GET /functions/v1/inbox/pending?limit=10&after=8", fake.Calls[2], StringComparison.Ordinal);

        using var cmd = t.Conn.CreateCommand();
        cmd.CommandText = "SELECT device_id || ' ' || status || ' ' || received_at || ' ' || (landed_at IS NOT NULL) FROM android_sync_log ORDER BY id";
        using var rows = cmd.ExecuteReader();
        Assert.True(rows.Read());
        Assert.Equal("00000000-fixture-device success 2026-11-20T18:30:00.123Z 1", rows.GetString(0));
        Assert.True(rows.Read());
        Assert.StartsWith("hint-2 rejected", rows.GetString(0), StringComparison.Ordinal);
    }

    [Fact]
    public void AGzipBombIsRefused()
    {
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var zipped = new MemoryStream();
        using (var gz = new GZipStream(zipped, CompressionLevel.SmallestSize, leaveOpen: true))
            gz.Write(new byte[9 * 1024 * 1024]);
        var envelope = Inbox.Seal(zipped.ToArray(), Inbox.PublicKeyB64(key), "token");
        Assert.Throws<InvalidDataException>(() => Inbox.EnvelopeJson(envelope, key, "token"));
    }

    // ---- Queries ----

    private static string Bucket(long uid, string day, string network, long rx, long tx) =>
        $$"""{"uid":{{uid}},"start":{{new DateTimeOffset(DateTime.Parse(day + "T00:00:00Z", null, System.Globalization.DateTimeStyles.AdjustToUniversal)).ToUnixTimeMilliseconds()}},"network":"{{network}}","metered":false,"roaming":false,"rx":{{rx}},"tx":{{tx}}}""";

    private static void Seed(TestDb t, params string[] buckets) => PhoneIngest.Ingest(t.Conn, PhoneIngest.Validate(Json($$"""
        {"deviceId":"{{Device}}","label":"Test Phone","utcOffsetMinutes":0,
         "apps":[{"uid":10001,"package":"com.example.a","label":"Alpha"},{"uid":10002,"package":"com.example.b","label":"Beta"},
                 {"uid":1000,"package":"com.google.android.backuptransport","label":"Backup","isSystem":true},
                 {"uid":1000,"package":"com.google.android.gms","label":"Google Play services","isSystem":true}],
         "buckets":[{{string.Join(",", buckets)}}]}
        """), new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero)));

    [Fact]
    public void TetheringIsKeptOutOfEveryTotalAndTheAppList()
    {
        using var t = new TestDb();
        Seed(t, Bucket(10001, "2026-11-01", "wifi", 100, 0), Bucket(-5, "2026-11-01", "mobile", 1000, 0));
        var q = new PhoneQueries(t.Path_, Device);
        Assert.Equal(100, q.Overview(Scope.All).All.Total);
        var apps = q.ByApp(Scope.All);
        Assert.Equal((100L, 1000L), (apps.NamedTotal, apps.Tethering));
        Assert.DoesNotContain(apps.Apps, a => a.Key == "-5");
        Assert.Equal(1000, q.Extras(Scope.All).Tethering);
    }

    [Fact]
    public void AQuietDayInsideTheHistoryIsZeroNotUnknown()
    {
        using var t = new TestDb();
        Seed(t, Bucket(10001, "2026-11-01", "wifi", 100, 0), Bucket(10001, "2026-11-04", "wifi", 50, 0));
        var daily = new PhoneQueries(t.Path_, Device).Overview(Scope.All).Daily;
        Assert.Equal(["2026-11-01", "2026-11-02", "2026-11-03", "2026-11-04"], daily.Select(d => d.Date));
        Assert.Equal([100L, 0, 0, 50], daily.Select(d => d.Total!.Value));
    }

    [Fact]
    public void ASharedUidIsNamedAfterThePackageThatNamesIt()
    {
        using var t = new TestDb();
        Seed(t, Bucket(1000, "2026-11-01", "wifi", 100, 0));
        Assert.Equal("Google Play services", new PhoneQueries(t.Path_, Device).ByApp(Scope.All).Apps.Single().Name);
    }

    [Fact]
    public void AMergedUidIsReadAsItsTargetWithItsDaysRecountedNotAdded()
    {
        using var t = new TestDb();
        Seed(t, Bucket(10001, "2026-11-01", "wifi", 100, 0), Bucket(10002, "2026-11-01", "wifi", 30, 0), Bucket(10002, "2026-11-02", "wifi", 20, 0));
        AppPrefs.SetMerge(t.Conn, "10002", "10001", new HashSet<string> { "10001", "10002" }, Device);
        var q = new PhoneQueries(t.Path_, Device);
        var app = q.ByApp(Scope.All).Apps.Single();
        Assert.Equal(("Alpha", 150L, 2), (app.Name, app.Total, app.Days));
        var detail = q.AppDetailFor("10001", Scope.All)!;
        Assert.Equal(2, detail.Members.Count);
        Assert.Equal(("10002", "Beta"), detail.Members.Single(m => m.MergedFrom is not null).MergedFrom);
        // The laptop's merges are a different device's: nothing leaks across.
        Assert.Empty(AppPrefs.ReadMerges(t.Conn));
    }

    [Fact]
    public void ARenameOnAPhoneIsThatPhonesAlone()
    {
        using var t = new TestDb();
        Seed(t, Bucket(10001, "2026-11-01", "wifi", 100, 0));
        var q = new PhoneQueries(t.Path_, Device);
        Assert.Equal(RenameError.None, Renames.Save(t.Conn, "10001", "My Alpha", q.AppNamesByKey(), "Alpha", Device).Error);
        Assert.Equal("My Alpha", q.ByApp(Scope.All).Apps.Single().Name);
        Assert.Empty(Renames.Read(t.Conn));
    }

    [Theory]
    [InlineData("Pixel 8", "pixel-8")]
    [InlineData("Acme Phone X2 Pro (5G)", "acme-phone-x2-pro-5g")]
    [InlineData("!!!", "device")]
    public void ASlugIsTheWebDashboards(string label, string slug) => Assert.Equal(slug, PhoneQueries.Slug(label));
}
