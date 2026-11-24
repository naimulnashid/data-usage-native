using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataUsage.Core.Data;
using DataUsage.Core.Srum;
using Microsoft.Data.Sqlite;

namespace DataUsage.Core.Android;

/// <summary>
/// Where the phones' cloud inbox is, in <c>settings.json</c>. Neither value is
/// secret: the address is public and the key file is only named here.
/// </summary>
public sealed class InboxSettings
{
    /// <summary>The Edge Function's address, <c>https://&lt;ref&gt;.supabase.co/functions/v1/inbox</c>.</summary>
    public string? Url { get; set; }

    /// <summary>PKCS#8 PEM of this PC's P-256 key. Never in the synced data folder.</summary>
    public string? KeyPath { get; set; }

    /// <summary>
    /// The two tokens, as JSON <c>{"drainToken": "...", "ingestToken": "..."}</c>;
    /// <c>inbox-tokens.json</c> beside the key when unset. Kept out of
    /// <c>settings.json</c> because the data folder is synced to the cloud,
    /// and the ingest token is what makes an envelope authentic.
    /// </summary>
    public string? TokensPath { get; set; }

    [JsonIgnore]
    public bool IsSet => !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(KeyPath);

    [JsonIgnore]
    public string ResolvedTokensPath => !string.IsNullOrWhiteSpace(TokensPath)
        ? TokensPath!
        : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(KeyPath!))!, "inbox-tokens.json");
}

public sealed record DrainOutcome(int Landed, int Failed, long RowsWritten);

/// <summary>
/// The cloud inbox, PC side: open sealed envelopes and empty them into the
/// database. A port of the web dashboard's <c>inbox.ts</c>, byte for byte.
/// </summary>
/// <remarks>
/// <para>Phones post to a fixed HTTPS address (a Supabase Edge Function) from
/// anywhere; this PC empties it whenever it runs, through exactly what a direct
/// upload went through (<see cref="PhoneIngest"/>). <b>The inbox can read
/// nothing</b>: each upload is sealed to a P-256 key whose private half lives
/// only on this machine.</para>
/// <code>
///   "DUI1"                 4 bytes   magic, and the version
///   ephemeral public key  65 bytes   uncompressed P-256 point, new per upload
///   nonce                 12 bytes
///   ciphertext + tag       rest      AES-256-GCM, 16-byte tag at the end
///
///   shared = ECDH(ephemeral, this PC's key)              x-coordinate, 32 bytes
///   salt   = HMAC-SHA256(ingest token, "data-usage inbox seal v1")
///   key    = HKDF-SHA256(shared, salt, "data-usage inbox v1" || ephemeral, 32)
///   aad    = magic || ephemeral
///   plain  = gzip(the same JSON a direct upload sends)
/// </code>
/// <para><b>The token in the salt is what makes an envelope authentic, not
/// just private.</b> The public key is public by definition, so anyone could
/// seal to it; only a holder of the ingest token derives a key this side
/// accepts. The inbox never sees that token: phones authenticate to it with
/// <see cref="UploadBearer"/>, an HMAC of it.</para>
/// <para>Replaying an old envelope is harmless, because the ingest upserts
/// with MAX().</para>
/// </remarks>
public static class Inbox
{
    public static ReadOnlySpan<byte> Magic => "DUI1"u8;
    private const int PointBytes = 65;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const string SealLabel = "data-usage inbox seal v1";
    private const string UploadLabel = "data-usage inbox upload v1";
    private const string InfoLabel = "data-usage inbox v1";

    /// <summary>Decompressed cap, the same as the web dashboard's direct upload limit.</summary>
    private const int MaxPlainBytes = 8 * 1024 * 1024;

    /// <summary>A key, ready to open envelopes. Dispose when done.</summary>
    public static ECDiffieHellman LoadKey(string keyPath) => KeyFromPem(File.ReadAllText(keyPath), keyPath);

    public static ECDiffieHellman KeyFromPem(string pem, string what = "the inbox key")
    {
        var key = ECDiffieHellman.Create();
        key.ImportFromPem(pem);
        if (key.KeySize != 256 || key.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
            throw new InvalidOperationException($"{what} is not a P-256 key");
        return key;
    }

    /// <summary>The public half as base64 SPKI DER: what a phone stores.</summary>
    public static string PublicKeyB64(ECDiffieHellman key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

    /// <summary>Sixteen hex digits of SHA-256 over the SPKI, in groups of four, as the phone shows it.</summary>
    public static string Fingerprint(string spkiB64)
    {
        var hex = Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(spkiB64)))[..16];
        return string.Join(' ', Enumerable.Range(0, 4).Select(i => hex.Substring(i * 4, 4)));
    }

    /// <summary>The bearer a phone presents to the inbox. The inbox stores its SHA-256.</summary>
    public static string UploadBearer(string token) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(UploadLabel))).ToLowerInvariant();

    private static byte[] AesKey(byte[] shared, ReadOnlySpan<byte> point, string token)
    {
        var salt = HMACSHA256.HashData(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(SealLabel));
        var info = Encoding.ASCII.GetBytes(InfoLabel).Concat(point.ToArray()).ToArray();
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, salt, info);
    }

    private static byte[] Aad(ReadOnlySpan<byte> point) => [.. Magic, .. point];

    /// <summary>
    /// Seal bytes to a key, as the phone does in Kotlin. Here for the tests
    /// and for checking a deployed inbox end to end.
    /// </summary>
    public static byte[] Seal(byte[] plain, string recipientSpkiB64, string token)
    {
        using var recipient = ECDiffieHellman.Create();
        recipient.ImportSubjectPublicKeyInfo(Convert.FromBase64String(recipientSpkiB64), out _);
        using var eph = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var q = eph.ExportParameters(false).Q;
        byte[] point = [0x04, .. q.X!, .. q.Y!];
        var key = AesKey(eph.DeriveRawSecretAgreement(recipient.PublicKey), point, token);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagBytes];
        using (var gcm = new AesGcm(key, TagBytes)) gcm.Encrypt(nonce, plain, cipher, tag, Aad(point));
        return [.. Magic, .. point, .. nonce, .. cipher, .. tag];
    }

    /// <summary>
    /// Open an envelope, or throw. A wrong key, a wrong token and a tampered
    /// byte all fail the same way, at the GCM tag, and read as one error.
    /// </summary>
    public static byte[] Open(byte[] envelope, ECDiffieHellman key, string token)
    {
        if (envelope.Length < Magic.Length + PointBytes + NonceBytes + TagBytes) throw new CryptographicException("envelope too short");
        if (!envelope.AsSpan(0, 4).SequenceEqual(Magic)) throw new CryptographicException("not a DUI1 envelope");
        var point = envelope.AsSpan(4, PointBytes);
        if (point[0] != 0x04) throw new CryptographicException("bad ephemeral key");
        var nonce = envelope.AsSpan(4 + PointBytes, NonceBytes);
        var body = envelope.AsSpan(4 + PointBytes + NonceBytes, envelope.Length - 4 - PointBytes - NonceBytes - TagBytes);
        var tag = envelope.AsSpan(envelope.Length - TagBytes);

        byte[] aes;
        try
        {
            using var eph = ECDiffieHellman.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = point[1..33].ToArray(), Y = point[33..].ToArray() },
            });
            aes = AesKey(key.DeriveRawSecretAgreement(eph.PublicKey), point, token);
        }
        catch (CryptographicException)
        {
            throw new CryptographicException("bad ephemeral key");
        }

        var plain = new byte[body.Length];
        try
        {
            using var gcm = new AesGcm(aes, TagBytes);
            gcm.Decrypt(nonce, body, tag, plain, Aad(point));
        }
        catch (CryptographicException)
        {
            throw new CryptographicException("cannot open envelope: sealed to another PC key, with another ingest token, or altered");
        }
        return plain;
    }

    /// <summary>Envelope to the payload JSON a direct upload would have carried.</summary>
    public static JsonDocument EnvelopeJson(byte[] envelope, ECDiffieHellman key, string token)
    {
        using var zipped = new MemoryStream(Open(envelope, key, token));
        using var gz = new GZipStream(zipped, CompressionMode.Decompress);
        using var plain = new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = gz.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (plain.Length + n > MaxPlainBytes) throw new InvalidDataException("upload larger than 8 MB once unpacked");
            plain.Write(buffer, 0, n);
        }
        return JsonDocument.Parse(plain.ToArray());
    }

    /// <summary>Reads the two tokens; throws with what is missing.</summary>
    public static (string Drain, string Ingest) LoadTokens(InboxSettings cfg)
    {
        var path = cfg.ResolvedTokensPath;
        if (!File.Exists(path)) throw new InvalidOperationException($"the inbox tokens file is missing: {path}");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        string? Get(string name) => doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString()!.Trim() is { Length: > 0 } s ? s : null;
        var drain = Get("drainToken") ?? throw new InvalidOperationException($"drainToken is not set in {path}");
        var ingest = Get("ingestToken") ?? throw new InvalidOperationException($"ingestToken is not set in {path}");
        return (drain, ingest);
    }

    private sealed record Pending(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("received_at")] string ReceivedAt,
        [property: JsonPropertyName("device_hint")] string? DeviceHint,
        [property: JsonPropertyName("envelope")] string Envelope);

    private sealed record Page([property: JsonPropertyName("envelopes")] List<Pending>? Envelopes);

    /// <summary>
    /// Empty the inbox into the database.
    /// </summary>
    /// <remarks>
    /// <para>Each envelope is logged with <c>received_at</c> = when the PHONE
    /// sent it and <c>landed_at</c> = now. It is acknowledged only after the
    /// write: a lost acknowledgement means the next drain ingests it again,
    /// which MAX() makes harmless (it costs a duplicate log row).</para>
    /// <para>One that cannot be opened or is rejected is marked failed in the
    /// inbox and skipped from then on, rather than retried and logged every
    /// run. <paramref name="retryFailed"/> tries those again.</para>
    /// <para>Only one drain may run against an inbox: whichever acknowledges
    /// an envelope first takes it, and the other never sees it.</para>
    /// </remarks>
    public static async Task<DrainOutcome> DrainAsync(SqliteConnection db, InboxSettings cfg, Action<string>? log = null,
        bool retryFailed = false, bool dryRun = false, HttpMessageHandler? handler = null, CancellationToken cancel = default)
    {
        log ??= _ => { };
        var url = cfg.Url!.TrimEnd('/');
        if (!url.StartsWith("https://", StringComparison.Ordinal)) throw new InvalidOperationException("the inbox address must be https://");
        var (drainToken, ingestToken) = LoadTokens(cfg);
        using var key = LoadKey(cfg.KeyPath!);

        using var http = handler is null
            ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            : new HttpClient(handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromSeconds(60);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", drainToken);

        async Task<JsonDocument> Call(HttpMethod method, string path, object? body = null)
        {
            using var req = new HttpRequestMessage(method, $"{url}/{path}");
            req.Content = body is null ? null : new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var res = await http.SendAsync(req, cancel);
            var text = await res.Content.ReadAsStringAsync(cancel);
            JsonDocument? doc = null;
            try { doc = JsonDocument.Parse(text); } catch (JsonException) { }
            var ok = doc is not null && doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
            if (!res.IsSuccessStatusCode || !ok)
            {
                var error = doc is not null && doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var e) ? e.ToString() : "";
                doc?.Dispose();
                throw new HttpRequestException($"inbox {path.Split('?')[0]}: HTTP {(int)res.StatusCode} {error}".Trim());
            }
            return doc!;
        }

        int landed = 0, failedCount = 0;
        long rows = 0;
        // Paged by id rather than by "whatever is left", so an envelope that
        // stays behind (a dry run, a retry that fails again) is never fetched twice.
        long after = 0;
        for (;;)
        {
            List<Pending> envelopes;
            using (var page = await Call(HttpMethod.Get, $"pending?limit=10&after={after}{(retryFailed ? "&failed=1" : "")}"))
                envelopes = page.RootElement.Deserialize<Page>()?.Envelopes ?? [];
            if (envelopes.Count == 0) break;

            var done = new List<long>();
            var failed = new List<object>();
            foreach (var e in envelopes)
            {
                after = Math.Max(after, e.Id);
                var receivedAt = SrumFields.IsoUtc(DateTimeOffset.Parse(e.ReceivedAt, System.Globalization.CultureInfo.InvariantCulture).UtcDateTime);
                var deviceId = e.DeviceHint;
                try
                {
                    using var json = EnvelopeJson(Convert.FromBase64String(e.Envelope), key, ingestToken);
                    var payload = PhoneIngest.Validate(json.RootElement);
                    deviceId = payload.DeviceId;
                    if (dryRun)
                    {
                        log($"#{e.Id} {deviceId}: opens, {payload.Buckets.Count} buckets (dry run)");
                        continue;
                    }
                    var r = PhoneIngest.Ingest(db, payload);
                    PhoneIngest.LogSync(db, payload.DeviceId, "success", r, payload.Buckets.Count, payload.AppVersion,
                        receivedAt: receivedAt, landedAt: UsageDb.NowIso());
                    landed++;
                    rows += r.Written;
                    done.Add(e.Id);
                    log($"#{e.Id} {deviceId}: {r.Written} new, {r.Updated} updated");
                }
                catch (Exception ex) when (ex is CryptographicException or PhoneIngest.InvalidPayloadException or JsonException
                                              or InvalidDataException or FormatException)
                {
                    log($"#{e.Id} {deviceId ?? "unknown phone"}: {ex.Message}");
                    if (dryRun) continue;
                    if (deviceId is not null)
                        PhoneIngest.LogSync(db, deviceId, "rejected", error: $"inbox: {ex.Message}", receivedAt: receivedAt, landedAt: UsageDb.NowIso());
                    failedCount++;
                    failed.Add(new { id = e.Id, error = ex.Message });
                }
            }

            if (!dryRun && (done.Count > 0 || failed.Count > 0))
                (await Call(HttpMethod.Post, "ack", new { done, failed })).Dispose();
        }
        return new DrainOutcome(landed, failedCount, rows);
    }
}
