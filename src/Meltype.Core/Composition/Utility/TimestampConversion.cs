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

        if (arg.Length == 0 || arg.Equals("now", StringComparison.OrdinalIgnoreCase))
            return FormatCurrent(now, preferUnix: name is "unix" or "epoch");

        if (TryParseRelativeKeyword(arg, now, out var fromKeyword))
            return fromKeyword;

        if (TryParseCompactDate(arg, now, out var fromCompact))
            return fromCompact;

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
            // 桁数で秒 / ミリ秒 / マイクロ秒 / ナノ秒を自動判定する
            var abs = Math.Abs(value);
            var utc = abs switch
            {
                >= 100_000_000_000_000_000L => DateTimeOffset.FromUnixTimeMilliseconds(value / 1_000_000), // ナノ秒 (18〜19桁)
                >= 100_000_000_000_000L => DateTimeOffset.FromUnixTimeMilliseconds(value / 1_000),        // マイクロ秒 (15〜17桁)
                >= 100_000_000_000L => DateTimeOffset.FromUnixTimeMilliseconds(value),                    // ミリ秒 (12〜14桁)
                _ => DateTimeOffset.FromUnixTimeSeconds(value),                                           // 秒
            };
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

    private static bool TryParseRelativeKeyword(string arg, DateTime now, out List<string> candidates)
    {
        candidates = [];
        var offsetDays = arg.ToLowerInvariant() switch
        {
            "today" => 0,
            "tomorrow" => 1,
            "yesterday" => -1,
            _ => int.MinValue,
        };
        if (offsetDays == int.MinValue) return false;
        var baseDto = ToDateTimeOffset(now);
        var targetDate = baseDto.Date.AddDays(offsetDays);
        candidates = FormatFromDateTimeOffset(new DateTimeOffset(targetDate, baseDto.Offset));
        return true;
    }

    private static bool TryParseCompactDate(string arg, DateTime now, out List<string> candidates)
    {
        candidates = [];
        if (arg.Length is not (8 or 14) || !arg.All(char.IsAsciiDigit)) return false;
        var format = arg.Length == 8 ? "yyyyMMdd" : "yyyyMMddHHmmss";
        if (!DateTime.TryParseExact(arg, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return false;
        if (dt.Year is < 1970 or > 2100) return false;
        var offset = now.Kind == DateTimeKind.Utc ? TimeSpan.Zero : TimeZoneInfo.Local.GetUtcOffset(dt);
        candidates = FormatFromDateTimeOffset(new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Unspecified), offset));
        return true;
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
