// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 aster-void

namespace Meltype.Composition.Utility;

/// <summary>
/// きょう に今日の日付を出す (issue #208)。
/// 2026年10月9日 / 2026年10月9日(金) / 10月9日(金) / 2026/10/09 / 2026/10/09(金) / 10/9(金) / 2026-10-09 / 金曜日 / 20261009 …
/// 区切りの / と : は、Windows の地域の設定に左右されないように数字から組み立てる (DateTime の書式の / : は地域の区切りになる)。
/// </summary>
internal sealed class TodayConversion : IUtilityConversion
{
    public UtilityCandidates? Convert(UtilityInput input) =>
        input.Reading == "きょう" ? new(DateCandidates(input.Context.Now), UtilityPlacement.Append) : null;

    internal static string[] DateCandidates(DateTime target)
    {
        var day = UtilityConversions.WeekDays[(int)target.DayOfWeek];
        var date = $"{target.Year}年{target.Month}月{target.Day}日";
        var monthDay = $"{target.Month}月{target.Day}日";
        var slashDate = $"{target.Year:D4}/{target.Month:D2}/{target.Day:D2}";
        return [date, $"{date}({day})", monthDay, $"{monthDay}({day})", slashDate, $"{slashDate}({day})", $"{target.Month}/{target.Day}({day})",
            $"{target.Year:D4}-{target.Month:D2}-{target.Day:D2}", $"{day}曜日", $"{target.Year:D4}{target.Month:D2}{target.Day:D2}"];
    }
}
