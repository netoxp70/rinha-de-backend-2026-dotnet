using System.Buffers;
using System.Globalization;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Shared;

namespace Api.Infrastructure;

/// <summary>
/// Zero-alloc request body parser.
/// Reads directly from PipeReader and parses JSON via Utf8JsonReader,
/// producing a Vector14F without allocating a DTO object.
/// Property matching uses span SequenceEqual on UTF-8 bytes — no heap strings.
/// </summary>
internal static class RequestParser
{
    // UTF-8 encoded property name literals used for span matching
    private static ReadOnlySpan<byte> B_transaction      => "transaction"u8;
    private static ReadOnlySpan<byte> B_customer         => "customer"u8;
    private static ReadOnlySpan<byte> B_merchant         => "merchant"u8;
    private static ReadOnlySpan<byte> B_terminal         => "terminal"u8;
    private static ReadOnlySpan<byte> B_last_transaction => "last_transaction"u8;
    private static ReadOnlySpan<byte> B_amount           => "amount"u8;
    private static ReadOnlySpan<byte> B_installments     => "installments"u8;
    private static ReadOnlySpan<byte> B_requested_at     => "requested_at"u8;
    private static ReadOnlySpan<byte> B_avg_amount       => "avg_amount"u8;
    private static ReadOnlySpan<byte> B_tx_count_24h     => "tx_count_24h"u8;
    private static ReadOnlySpan<byte> B_known_merchants  => "known_merchants"u8;
    private static ReadOnlySpan<byte> B_id               => "id"u8;
    private static ReadOnlySpan<byte> B_mcc              => "mcc"u8;
    private static ReadOnlySpan<byte> B_is_online        => "is_online"u8;
    private static ReadOnlySpan<byte> B_card_present     => "card_present"u8;
    private static ReadOnlySpan<byte> B_km_from_home     => "km_from_home"u8;
    private static ReadOnlySpan<byte> B_timestamp        => "timestamp"u8;
    private static ReadOnlySpan<byte> B_km_from_current  => "km_from_current"u8;

    /// <summary>
    /// Reads the PipeReader body and parses it into a Vector14F.
    /// Returns (true, vector) on success, (false, default) on malformed input.
    /// Uses a tuple return to avoid the async-out-parameter restriction.
    /// </summary>
    public static async ValueTask<(bool Ok, Vector14F Query)> TryParseAsync(
        PipeReader pipe,
        CancellationToken ct = default)
    {
        ReadResult result;
        try
        {
            // Fast path: body already in pipe buffer (keep-alive, buffered request) — no await needed.
            if (!pipe.TryRead(out result))
                result = await pipe.ReadAsync(ct);
        }
        catch
        {
            return (false, default);
        }

        var buf = result.Buffer;
        bool ok;
        Vector14F query;

        if (buf.IsSingleSegment)
        {
            ok = Parse(buf.FirstSpan, out query);
        }
        else
        {
            // Multi-segment fallback (very rare for <4KB bodies)
            int len = (int)buf.Length;
            byte[] rented = ArrayPool<byte>.Shared.Rent(len);
            buf.CopyTo(rented);
            ok = Parse(rented.AsSpan(0, len), out query);
            ArrayPool<byte>.Shared.Return(rented);
        }

        pipe.AdvanceTo(buf.End);
        return (ok, query);
    }

    private static bool Parse(ReadOnlySpan<byte> json, out Vector14F query)
    {
        query = default;

        // ── Mutable parse state ───────────────────────────────────────────────
        float  txAmount          = 0f;
        int    installments      = 0;
        long   requestedAtTicks  = 0;
        float  custAvgAmount     = 0f;
        int    txCount24h        = 0;

        // Store hashes of known_merchants entries to avoid string allocs.
        // Up to 32 entries on stack.
        Span<ulong> kmHashes    = stackalloc ulong[32];
        int         kmCount     = 0;
        ulong       merchantHash = 0;

        // MCC is max 4 ASCII chars; keep on stack
        Span<byte> mccBytes    = stackalloc byte[8];
        int        mccLen      = 0;

        float merchantAvgAmount = 0f;
        bool  isOnline          = false;
        bool  cardPresent       = false;
        float kmFromHome        = 0f;

        bool  hasLastTx         = false;
        long  lastTxTicks       = 0;
        float kmFromCurrent     = 0f;

        // ── State machine ─────────────────────────────────────────────────────
        var reader  = new Utf8JsonReader(json, isFinalBlock: true, default);
        int depth   = 0;
        var section = Sec.None;
        bool inKnownMerchants = false;

        try
        {
            while (reader.Read())
            {
                var tok = reader.TokenType;

                if (tok == JsonTokenType.StartObject)  { depth++; continue; }
                if (tok == JsonTokenType.EndArray)     { depth--; inKnownMerchants = false; continue; }
                if (tok == JsonTokenType.StartArray)   { depth++; continue; }

                if (tok == JsonTokenType.EndObject)
                {
                    depth--;
                    if (depth == 1) { section = Sec.None; inKnownMerchants = false; }
                    continue;
                }

                // ── String values inside known_merchants array ────────────────
                if (tok == JsonTokenType.String && inKnownMerchants)
                {
                    if (kmCount < kmHashes.Length)
                        kmHashes[kmCount++] = FnvHash(reader.ValueSpan);
                    continue;
                }

                // ── Null — last_transaction: null ─────────────────────────────
                if (tok == JsonTokenType.Null && section == Sec.LastTx)
                {
                    hasLastTx = false;
                    continue;
                }

                if (tok != JsonTokenType.PropertyName) continue;

                var prop = reader.ValueSpan;

                // depth==1 → top-level section selector (property of root object)
                if (depth == 1)
                {
                    if      (prop.SequenceEqual(B_transaction))      section = Sec.Transaction;
                    else if (prop.SequenceEqual(B_customer))         section = Sec.Customer;
                    else if (prop.SequenceEqual(B_merchant))         section = Sec.Merchant;
                    else if (prop.SequenceEqual(B_terminal))         section = Sec.Terminal;
                    else if (prop.SequenceEqual(B_last_transaction)) section = Sec.LastTx;
                    else                                             section = Sec.None;
                    continue;
                }

                // depth==2 → field inside section object
                if (depth != 2) continue;

                switch (section)
                {
                    case Sec.Transaction:
                        if (prop.SequenceEqual(B_amount))
                        {
                            reader.Read();
                            txAmount = (float)reader.GetDouble();
                        }
                        else if (prop.SequenceEqual(B_installments))
                        {
                            reader.Read();
                            installments = reader.GetInt32();
                        }
                        else if (prop.SequenceEqual(B_requested_at))
                        {
                            reader.Read();
                            if (reader.TokenType == JsonTokenType.String)
                                requestedAtTicks = ParseDateTicks(reader.ValueSpan);
                        }
                        break;

                    case Sec.Customer:
                        if (prop.SequenceEqual(B_avg_amount))
                        {
                            reader.Read();
                            custAvgAmount = (float)reader.GetDouble();
                        }
                        else if (prop.SequenceEqual(B_tx_count_24h))
                        {
                            reader.Read();
                            txCount24h = reader.GetInt32();
                        }
                        else if (prop.SequenceEqual(B_known_merchants))
                        {
                            inKnownMerchants = true;
                        }
                        break;

                    case Sec.Merchant:
                        if (prop.SequenceEqual(B_id))
                        {
                            reader.Read();
                            if (reader.TokenType == JsonTokenType.String)
                                merchantHash = FnvHash(reader.ValueSpan);
                        }
                        else if (prop.SequenceEqual(B_mcc))
                        {
                            reader.Read();
                            if (reader.TokenType == JsonTokenType.String)
                            {
                                var vs = reader.ValueSpan;
                                mccLen = Math.Min(vs.Length, mccBytes.Length);
                                vs[..mccLen].CopyTo(mccBytes);
                            }
                        }
                        else if (prop.SequenceEqual(B_avg_amount))
                        {
                            reader.Read();
                            merchantAvgAmount = (float)reader.GetDouble();
                        }
                        break;

                    case Sec.Terminal:
                        if (prop.SequenceEqual(B_is_online))
                        {
                            reader.Read();
                            isOnline = reader.GetBoolean();
                        }
                        else if (prop.SequenceEqual(B_card_present))
                        {
                            reader.Read();
                            cardPresent = reader.GetBoolean();
                        }
                        else if (prop.SequenceEqual(B_km_from_home))
                        {
                            reader.Read();
                            kmFromHome = (float)reader.GetDouble();
                        }
                        break;

                    case Sec.LastTx:
                        if (prop.SequenceEqual(B_timestamp))
                        {
                            reader.Read();
                            if (reader.TokenType == JsonTokenType.String)
                            {
                                long t = ParseDateTicks(reader.ValueSpan);
                                if (t > 0) { lastTxTicks = t; hasLastTx = true; }
                            }
                        }
                        else if (prop.SequenceEqual(B_km_from_current))
                        {
                            reader.Read();
                            kmFromCurrent = (float)reader.GetDouble();
                        }
                        break;
                }
            }
        }
        catch
        {
            return false;
        }

        // ── Derive hour / dow from requestedAt ────────────────────────────────
        var utcDt = new DateTime(requestedAtTicks, DateTimeKind.Utc);
        int hour = utcDt.Hour;
        int dow  = (int)utcDt.DayOfWeek; // Sun=0 … Sat=6; matches Vectorizer contract

        // Remap to Mon=0…Sun=6 to be consistent with existing Program.cs logic
        dow = dow == 0 ? 6 : dow - 1;

        // ── Minutes since last tx / km from last ──────────────────────────────
        float? minSinceLast = null;
        float? kmFromLast   = null;
        if (hasLastTx)
        {
            if (lastTxTicks > 0)
            {
                double mins = (requestedAtTicks - lastTxTicks) / (double)TimeSpan.TicksPerMinute;
                minSinceLast = mins < 0.0 ? 0f : (float)mins;
            }
            kmFromLast = kmFromCurrent;
        }

        // ── Unknown merchant check ─────────────────────────────────────────────
        bool unknownMerchant = true;
        for (int i = 0; i < kmCount; i++)
        {
            if (kmHashes[i] == merchantHash) { unknownMerchant = false; break; }
        }

        // ── MCC risk lookup ───────────────────────────────────────────────────
        // Use span-based lookup to avoid string allocation
        float mccRisk = MccRiskSpan(mccBytes[..mccLen]);

        query = Vectorizer.Vectorize(
            txAmount, installments, custAvgAmount,
            hour, dow,
            minSinceLast, kmFromLast,
            kmFromHome, txCount24h,
            isOnline, cardPresent, unknownMerchant,
            mccRisk, merchantAvgAmount);

        return true;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong FnvHash(ReadOnlySpan<byte> data)
    {
        ulong h = 14695981039346656037UL;
        for (int i = 0; i < data.Length; i++)
            h = (h ^ data[i]) * 1099511628211UL;
        return h;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ParseDateTicks(ReadOnlySpan<byte> utf8)
    {
        // Fast-path: ISO-8601 like "2026-03-11T18:45:53Z" (20 chars)
        // Parse manually to avoid allocating a string.
        if (utf8.Length >= 19)
        {
            int yr  = Digit(utf8[0])*1000 + Digit(utf8[1])*100 + Digit(utf8[2])*10 + Digit(utf8[3]);
            int mo  = Digit(utf8[5])*10 + Digit(utf8[6]);
            int dy  = Digit(utf8[8])*10 + Digit(utf8[9]);
            int hr  = Digit(utf8[11])*10 + Digit(utf8[12]);
            int min = Digit(utf8[14])*10 + Digit(utf8[15]);
            int sec = Digit(utf8[17])*10 + Digit(utf8[18]);

            if (mo >= 1 && mo <= 12 && dy >= 1 && dy <= 31 &&
                hr <= 23 && min <= 59 && sec <= 60)
            {
                try
                {
                    return new DateTime(yr, mo, dy, hr, min, sec, DateTimeKind.Utc).Ticks;
                }
                catch { /* fall through to slow path */ }
            }
        }

        // Slow path: allocate string and use DateTimeOffset.TryParse
        Span<char> chars = stackalloc char[Math.Min(utf8.Length, 32)];
        int clen = Encoding.UTF8.GetChars(utf8[..Math.Min(utf8.Length, 32)], chars);
        if (DateTimeOffset.TryParse(chars[..clen], CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var dto))
            return dto.UtcTicks;

        return 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Digit(byte b) => b - '0';

    /// <summary>Span-based MCC risk lookup — no string alloc.</summary>
    private static float MccRiskSpan(ReadOnlySpan<byte> mcc)
    {
        if (mcc.Length != 4) return MccRisk.DefaultRisk;

        // Compare as 4-byte uint for speed
        uint v = (uint)mcc[0] << 24 | (uint)mcc[1] << 16 | (uint)mcc[2] << 8 | mcc[3];
        return v switch
        {
            0x35343131 => 0.15f, // "5411"
            0x35383132 => 0.30f, // "5812"
            0x35393132 => 0.20f, // "5912"
            0x35393434 => 0.45f, // "5944"
            0x37383031 => 0.80f, // "7801"
            0x37383032 => 0.75f, // "7802"
            0x37393935 => 0.85f, // "7995"
            0x34353131 => 0.35f, // "4511"
            0x35333131 => 0.25f, // "5311"
            0x35393939 => 0.50f, // "5999"
            _          => MccRisk.DefaultRisk,
        };
    }

    private enum Sec : byte
    {
        None, Transaction, Customer, Merchant, Terminal, LastTx
    }
}
