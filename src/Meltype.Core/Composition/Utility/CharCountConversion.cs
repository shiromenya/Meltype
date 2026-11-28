// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 shiromenya

using System.Globalization;
using System.Text;

namespace Meltype.Composition.Utility;

/// <summary>
/// 「もじすう」を変換すると、選択中の文字列またはキャレット直前の本文の文字数を数えて候補に出す。
/// 確認だけなら Esc で消せ、そのまま確定すれば文字数を入力できる。
/// </summary>
internal sealed class CharCountConversion : IUtilityConversion
{
    private const int TsfBeforeLimit = 4000;

    public UtilityCandidates? Convert(UtilityInput input)
    {
        if (input.Reading is not "もじすう") return null;

        var hasSelection = !string.IsNullOrEmpty(input.Context.Selection);
        var target = hasSelection ? input.Context.Selection : input.Context.Before;
        if (target is null) return null;

        var (total, noSpace, lines) = Count(target);
        var truncated = !hasSelection && target.Length >= TsfBeforeLimit;

        var candidates = new List<string>(4);
        candidates.Add(truncated ? $"{total}文字以上 (直前{TsfBeforeLimit}字上限)" : $"{total}文字");
        if (noSpace != total)
            candidates.Add($"{noSpace}文字 (空白除く)");
        if (lines > 1)
            candidates.Add($"{total}文字 ({lines}行)");
        candidates.Add(total.ToString(CultureInfo.InvariantCulture));

        return new(candidates.Distinct(StringComparer.Ordinal).ToList(), UtilityPlacement.Append);
    }

    internal static (int Total, int NoSpace, int Lines) Count(string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var total = 0;
        var noSpace = 0;
        var lines = 1;

        var enumerator = StringInfo.GetTextElementEnumerator(normalized);
        while (enumerator.MoveNext())
        {
            var element = enumerator.GetTextElement();
            if (element == "\n")
            {
                lines++;
                continue;
            }
            total++;
            if (!IsAllWhiteSpace(element))
                noSpace++;
        }

        return (total, noSpace, lines);
    }

    private static bool IsAllWhiteSpace(string textElement)
    {
        foreach (var rune in textElement.EnumerateRunes())
        {
            if (!Rune.IsWhiteSpace(rune)) return false;
        }
        return true;
    }
}
