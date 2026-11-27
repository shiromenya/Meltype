// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 shiromenya

using System.Globalization;

namespace Meltype.Composition.Utility;

/// <summary>
/// UNIX タイムスタンプと ISO 8601 / 日時の相互変換。
/// - `iso` / `unix` / `epoch` / `たいむすたんぷ` / `えぽっく`: 通常候補の後ろに現在時刻の各形式を足す (Append)
/// - `iso()` / `unix()` / `epoch()`: 現在時刻の各形式だけを候補に出す (Replace)
/// - `unix(数値)` / `epoch(数値)`: UNIX 秒・ミリ秒 (12 桁以上) を日時へ変換 (Replace)
/// - `unix(日時)` / `epoch(日時)` / `iso(日時)`: 日時文字列を UNIX 秒・ミリ秒・ISO 8601 へ変換 (Replace)
/// </summary>
internal sealed class TimestampConversion : IUtilityConversion
{
    public UtilityCandidates? Convert(UtilityInput input)
    {
        var raw = CompositionText.Raw(input.Text);
        if (!input.Text.KanaInput && TryConvertFunction(raw, input.Context.Now) is { Count: > 0 } funcCandidates)
            return new(funcCandidates, UtilityPlacement.Replace) { Learnable = false };

        if (input.Reading is "たいむすたんぷ" or "えぽっく" ||
            (!input.Text.KanaInput && raw.ToLowerInvariant() is "iso" or "unix" or "epoch"))
        {
            var candidates = FormatCurrent(input.Context.Now, preferUnix: !raw.Equals("iso", StringComparison.OrdinalIgnoreCase) && input.Reading != "たいむすたんぷ");
            return new(candidates, UtilityPlacement.Append) { Learnable = false };
        }
        return null;
    }

    /// <summary>予測変換 (Tab) 用: 関数記法 `iso()` / `unix(...)` / `epoch(...)` に一致したときだけ候補を返す。</summary>
    internal static IReadOnlyList<string>? PredictionCandidates(string raw, DateTime now) =>
        TryConvertFunction(raw, now);

    /// <summary>関数記法 `iso()` / `unix(...)` / `epoch(...)` に一致するか (Space での変換開始判定用)。</summary>
    internal static bool Matches(string raw, DateTime now) =>
        TryConvertFunction(raw, now) is { Count: > 0 };

    private static List<string>? TryConvertFunction(string raw, DateTime now)
    {
        var trimmed = raw.Trim();
        var open = trimmed.IndexOf('(');
        if (open <= 0 || !trimmed.EndsWith(')')) return null;
        var name = trimmed[..open].ToLowerInvariant();
        if (name is not ("iso" or "unix" or "epoch")) return null;
        var arg = ToHalfWidthAscii(trimmed[(open + 1)..^1]).Trim();

        if (arg.Length == 0)
            return FormatCurrent(now, preferUnix: name is "unix" or "epoch");

        if (name is "unix" or "epoch" && TryParseUnix(arg, now, out var fromUnix))
            return fromUnix;

        if (TryParseDateTime(arg, now, out var fromDate))
            return fromDate;

        return null;
    }

    private static List<string> FormatCurrent(DateTime now, bool preferUnix)
    {
        var dto = ToDateTimeOffset(now);
        var isoLocal = dto.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
        var isoUtc = dto.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var unixSec = dto.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var unixMs = dto.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var readable = dto.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        return Distinct(preferUnix
            ? [unixSec, unixMs, isoLocal, isoUtc, readable]
            : [isoLocal, isoUtc, unixSec, unixMs, readable]);
    }

    private static bool TryParseUnix(string arg, DateTime now, out List<string> candidates)
    {
        candidates = [];
        if (!long.TryParse(arg, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
            return false;

        try
        {
            // 絶対値 12 桁以上 (100_000_000_000 = 1973-03-03 以降のミリ秒相当) はミリ秒、それ未満は秒として扱う
            var utc = Math.Abs(value) >= 100_000_000_000L
                ? DateTimeOffset.FromUnixTimeMilliseconds(value)
                : DateTimeOffset.FromUnixTimeSeconds(value);
            var offset = now.Kind == DateTimeKind.Utc ? TimeSpan.Zero : TimeZoneInfo.Local.GetUtcOffset(utc.UtcDateTime);
            var local = utc.ToOffset(offset);
            candidates = Distinct([
                local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                local.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
                utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ]);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static bool TryParseDateTime(string arg, DateTime now, out List<string> candidates)
    {
        candidates = [];
        // オフセット (Z / +09:00) を含むときはそのまま解釈し、含まないときはローカル時刻としてオフセットを補う
        if (arg.EndsWith('Z') || arg.EndsWith('z') || HasExplicitOffset(arg))
        {
            if (!DateTimeOffset.TryParse(arg, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
                return false;
            candidates = FormatFromDateTimeOffset(dto);
            return true;
        }

        if (!DateTime.TryParse(arg, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var dt))
            return false;
        var offset = now.Kind == DateTimeKind.Utc ? TimeSpan.Zero : TimeZoneInfo.Local.GetUtcOffset(dt);
        var localDto = new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Unspecified), offset);
        candidates = FormatFromDateTimeOffset(localDto);
        return true;
    }

    private static List<string> FormatFromDateTimeOffset(DateTimeOffset dto) => Distinct([
        dto.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
        dto.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
        dto.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
        dto.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
    ]);

    private static bool HasExplicitOffset(string s)
    {
        var t = s.IndexOfAny(['T', 't', ' ']);
        if (t < 0) return false;
        var timePart = s[(t + 1)..];
        return timePart.Contains('+') || timePart.Contains('-');
    }

    private static DateTimeOffset ToDateTimeOffset(DateTime now) => now.Kind switch
    {
        DateTimeKind.Utc => new DateTimeOffset(now, TimeSpan.Zero),
        DateTimeKind.Local => new DateTimeOffset(now),
        _ => new DateTimeOffset(now, TimeZoneInfo.Local.GetUtcOffset(now)),
    };

    private static string ToHalfWidthAscii(string s) =>
        new(s.Select(c => c is >= '！' and <= '～' ? (char)(c - 0xFEE0) : c == '　' ? ' ' : c).ToArray());

    private static List<string> Distinct(IEnumerable<string> items) => items.Distinct(StringComparer.Ordinal).ToList();
}
