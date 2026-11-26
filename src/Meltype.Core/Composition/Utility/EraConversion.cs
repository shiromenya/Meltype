// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 shiromenya

using System.Text.RegularExpressions;

namespace Meltype.Composition.Utility;

/// <summary>
/// 西暦と和暦の年 (1995ねん・へいせい7ねん・れいわがんねん) を相互変換し、今年何歳かの目安も候補に出す。
/// 普通の候補の後ろに足し、学習には残さない。
/// </summary>
internal sealed partial class EraConversion : IUtilityConversion
{
    private readonly record struct Era(string Reading, string Name, int StartYear, int MaxYear);

    // 新しい元号を先に並べる (改元の年は新しい元号・前の元号の順に出す)
    private static readonly Era[] Eras =
    [
        new("れいわ", "令和", 2019, 99),
        new("へいせい", "平成", 1989, 31),
        new("しょうわ", "昭和", 1926, 64),
        new("たいしょう", "大正", 1912, 15),
        new("めいじ", "明治", 1868, 45),
    ];

    [GeneratedRegex(@"^(?<y>[0-9０-９]{4})ねん$")]
    private static partial Regex WesternYearReading();

    [GeneratedRegex(@"^(?<era>れいわ|へいせい|しょうわ|たいしょう|めいじ)(?<y>がん|[0-9０-９]{1,2})ねん$")]
    private static partial Regex JapaneseYearReading();

    public UtilityCandidates? Convert(UtilityInput input)
    {
        if (input.Reading is not { } reading) return null;
        var candidates = Candidates(reading, input.Context.Now);
        return candidates.Count > 0 ? new(candidates, UtilityPlacement.Append) : null;
    }

    internal static List<string> Candidates(string reading, DateTime now)
    {
        if (WesternYearReading().Match(reading) is { Success: true } western)
        {
            var year = UtilityConversions.Number(western.Groups["y"].Value);
            if (year is < 1868 or > 2100) return [];
            var results = new List<string>();
            foreach (var era in Eras)
            {
                var eraYear = year - era.StartYear + 1;
                if (eraYear >= 1 && eraYear <= era.MaxYear)
                    results.Add(eraYear == 1 ? $"{era.Name}元年" : $"{era.Name}{eraYear}年");
            }
            AddAgeCandidates(results, year, now.Year);
            return results;
        }

        if (JapaneseYearReading().Match(reading) is { Success: true } japanese)
        {
            var eraName = japanese.Groups["era"].Value;
            var era = Array.Find(Eras, e => e.Reading == eraName);
            var yText = japanese.Groups["y"].Value;
            var eraYear = yText == "がん" ? 1 : UtilityConversions.Number(yText);
            if (eraYear < 1 || eraYear > era.MaxYear) return [];
            var westernYear = era.StartYear + eraYear - 1;
            var results = new List<string> { $"{westernYear}年" };
            AddAgeCandidates(results, westernYear, now.Year);
            return results;
        }

        return [];
    }

    private static void AddAgeCandidates(List<string> results, int birthYear, int currentYear)
    {
        if (birthYear < 1900 || birthYear > currentYear) return;
        var age = currentYear - birthYear;
        if (age == 0)
        {
            results.Add("0歳");
            return;
        }
        results.Add($"今年{age}歳（{age - 1}〜{age}歳）");
    }
}
