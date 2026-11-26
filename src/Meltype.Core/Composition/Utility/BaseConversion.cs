// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 shiromenya

using System.Globalization;

namespace Meltype.Composition.Utility;

/// <summary>
/// 数字だけの入力を 2〜16 進数 (10 進数は除く) に変換し、「値(n進数)」の形で候補に出す。
/// 普通の変換候補・漢数字の後ろに足し、学習には残さない。
/// </summary>
internal sealed class BaseConversion : IUtilityConversion
{
    private const string BaseDigits = "0123456789ABCDEF";

    public UtilityCandidates? Convert(UtilityInput input) =>
        input.Reading is { } reading && Candidates(reading) is { Count: > 0 } candidates
            ? new(candidates, UtilityPlacement.Append)
            : null;

    internal static List<string> Candidates(string text)
    {
        var half = string.Concat(text.Replace(",", "").Select(c => c is >= '０' and <= '９' ? (char)(c - '０' + '0') : c));
        // 先頭が 0 の複数桁 (001 など) や 17 桁以上は対象外にする
        if (half.Length is 0 or > 16 || !half.All(char.IsAsciiDigit) || (half.Length > 1 && half[0] == '0'))
            return [];

        var value = ulong.Parse(half, CultureInfo.InvariantCulture);
        if (value < 2) return [];

        var result = new List<string>(14);
        for (var radix = 2; radix <= 16; radix++)
        {
            if (radix == 10) continue;
            result.Add($"{FormatBase(value, radix)}({radix}進数)");
        }
        return result;
    }

    internal static string FormatBase(ulong value, int radix)
    {
        if (value == 0) return "0";
        Span<char> buffer = stackalloc char[64];
        var index = buffer.Length;
        var baseVal = (ulong)radix;
        while (value > 0)
        {
            buffer[--index] = BaseDigits[(int)(value % baseVal)];
            value /= baseVal;
        }
        return new string(buffer[index..]);
    }
}
