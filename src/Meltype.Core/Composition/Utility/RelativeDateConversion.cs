// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 shiromenya

using System.Text.RegularExpressions;

namespace Meltype.Composition.Utility;

/// <summary>
/// 相対的な日付表現 (あした・3にちご・2しゅうかんまえ・らいしゅうのきんようび・こんげつまつ など) に、
/// 実際の日付候補を出す。普通の変換候補の後ろに足し、学習には残さない。
/// </summary>
internal sealed partial class RelativeDateConversion : IUtilityConversion
{
    [GeneratedRegex(@"^(?<n>[0-9０-９]{1,4})(?<unit>にち|しゅうかん|かげつ)(?<dir>ご|まえ)$")]
    private static partial Regex NumericRelative();

    [GeneratedRegex(@"^(?<week>せんせんしゅう|せんしゅう|こんしゅう|さらいしゅう|らいしゅう)(?:の)?(?<day>げつ|か|すい|もく|きん|ど|にち)よう(?:び)?$")]
    private static partial Regex WeekAndDay();

    private static readonly Dictionary<string, int> NamedDayOffsets = new(StringComparer.Ordinal)
    {
        ["おととい"] = -2,
        ["きのう"] = -1,
        ["あした"] = 1,
        ["あす"] = 1,
        ["あさって"] = 2,
        ["しあさって"] = 3,
    };

    private static readonly Dictionary<string, int> WagoDayCounts = new(StringComparer.Ordinal)
    {
        ["ふつか"] = 2,
        ["みっか"] = 3,
        ["よっか"] = 4,
        ["いつか"] = 5,
        ["むいか"] = 6,
        ["なのか"] = 7,
        ["ようか"] = 8,
        ["ここのか"] = 9,
        ["とおか"] = 10,
    };

    public UtilityCandidates? Convert(UtilityInput input)
    {
        if (input.Reading is not { } reading) return null;
        if (ResolveDate(reading, input.Context.Now) is not { } target) return null;
        return new(TodayConversion.DateCandidates(target), UtilityPlacement.Append);
    }

    internal static DateTime? ResolveDate(string reading, DateTime now)
    {
        var today = now.Date;

        if (NamedDayOffsets.TryGetValue(reading, out var namedOffset))
            return today.AddDays(namedOffset);

        if (reading is "こんげつまつ" or "らいげつまつ" or "せんげつまつ")
        {
            var monthOffset = reading switch
            {
                "せんげつまつ" => -1,
                "らいげつまつ" => 1,
                _ => 0,
            };
            var firstOfMonth = new DateTime(today.Year, today.Month, 1).AddMonths(monthOffset);
            return new DateTime(firstOfMonth.Year, firstOfMonth.Month, DateTime.DaysInMonth(firstOfMonth.Year, firstOfMonth.Month));
        }

        if (reading.EndsWith('ご') || reading.EndsWith("まえ", StringComparison.Ordinal))
        {
            var sign = reading.EndsWith('ご') ? 1 : -1;
            var stem = reading[..^(sign > 0 ? 1 : 2)];
            if (WagoDayCounts.TryGetValue(stem, out var wagoDays))
                return today.AddDays(sign * wagoDays);
        }

        if (NumericRelative().Match(reading) is { Success: true } numMatch)
        {
            var count = UtilityConversions.Number(numMatch.Groups["n"].Value);
            var sign = numMatch.Groups["dir"].Value == "ご" ? 1 : -1;
            return numMatch.Groups["unit"].Value switch
            {
                "にち" when count is >= 1 and <= 3650 => today.AddDays(sign * count),
                "しゅうかん" when count is >= 1 and <= 520 => today.AddDays(sign * count * 7),
                "かげつ" when count is >= 1 and <= 120 => today.AddMonths(sign * count),
                _ => null,
            };
        }

        if (WeekAndDay().Match(reading) is { Success: true } weekMatch)
        {
            var weekOffset = weekMatch.Groups["week"].Value switch
            {
                "せんせんしゅう" => -14,
                "せんしゅう" => -7,
                "こんしゅう" => 0,
                "らいしゅう" => 7,
                "さらいしゅう" => 14,
                _ => 0,
            };
            // 日本のビジネス習慣に合わせて月曜始まり (月=0 .. 日=6) で今週の月曜日を基準にする
            var dayOffset = weekMatch.Groups["day"].Value switch
            {
                "げつ" => 0,
                "か" => 1,
                "すい" => 2,
                "もく" => 3,
                "きん" => 4,
                "ど" => 5,
                "にち" => 6,
                _ => 0,
            };
            var daysSinceMonday = ((int)today.DayOfWeek + 6) % 7;
            var monday = today.AddDays(-daysSinceMonday);
            return monday.AddDays(weekOffset + dayOffset);
        }

        return null;
    }
}
