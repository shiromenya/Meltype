// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 shiromenya

using System.Text;
using System.Text.RegularExpressions;

namespace Meltype.Composition;

/// <summary>文章推敲の結果。Text は書き換え後の文、Reasons は適用した推敲の理由、Patterns は適用したパターン識別子。</summary>
internal sealed record ProofreadResult(string Text, IReadOnlyList<string> Reasons, IReadOnlyList<string> Patterns);

/// <summary>
/// 変換した日本語の文に対して、冗長表現・二重表現・ら抜き言葉・重複した助詞・定番の誤用を軽く整える文章推敲。
/// 引用 (「…」・『…』・"…") の内側や URL・英字だけの文は書き換えない。
/// </summary>
internal static partial class Proofread
{
    private readonly record struct Rule(string Pattern, string Replacement, string Reason);

    // 長い表現・活用形から先に当てて、部分一致で壊れないようにする。
    private static readonly Rule[] ExactRules =
    [
        // 冗長表現
        new("することができます", "できます", "冗長表現"),
        new("することができました", "できました", "冗長表現"),
        new("することができません", "できません", "冗長表現"),
        new("することができる", "できる", "冗長表現"),
        new("することができた", "できた", "冗長表現"),
        new("することができない", "できない", "冗長表現"),
        new("ということができます", "といえます", "冗長表現"),
        new("ということができる", "といえる", "冗長表現"),

        // 二重表現
        new("まず最初に", "最初に", "二重表現"),
        new("一番最初", "最初", "二重表現"),
        new("一番最後", "最後", "二重表現"),
        new("あとで後悔", "後悔", "二重表現"),
        new("後で後悔", "後悔", "二重表現"),
        new("過半数以上", "過半数", "二重表現"),
        new("過半数を超える", "半数を超える", "二重表現"),
        new("頭痛が痛い", "頭が痛い", "二重表現"),
        new("腹痛が痛い", "腹が痛い", "二重表現"),
        new("違和感を感じる", "違和感を覚える", "二重表現"),
        new("被害を被る", "被害を受ける", "二重表現"),
        new("犯罪を犯す", "罪を犯す", "二重表現"),
        new("元旦の朝", "元旦", "二重表現"),
        new("あらかじめ予約", "予約", "二重表現"),
        new("予め予約", "予約", "二重表現"),
        new("まだ未定", "未定", "二重表現"),
        new("まだ未完成", "未完成", "二重表現"),

        // 定番の誤用
        new("的を得ました", "的を射ました", "誤用"),
        new("的を得た", "的を射た", "誤用"),
        new("的を得て", "的を射て", "誤用"),
        new("的を得る", "的を射る", "誤用"),
        new("汚名挽回", "汚名返上", "誤用"),

        // ら抜き言葉 (よく使われる一段動詞・カ変動詞の終止形・連体形・丁寧形)
        .. BuildRaNukiRules(
            ["見", "食べ", "来", "出", "寝", "起き", "着", "考え", "覚え", "決め", "開け", "止め"]),
    ];

    private static IEnumerable<Rule> BuildRaNukiRules(string[] stems)
    {
        (string From, string To)[] endings =
        [
            ("れました", "られました"),
            ("れません", "られません"),
            ("れます", "られます"),
            ("れない", "られない"),
            ("れた", "られた"),
            ("れる", "られる"),
        ];
        foreach (var stem in stems)
        {
            foreach (var (from, to) in endings)
                yield return new Rule(stem + from, stem + to, "ら抜き言葉");
        }
    }

    // 「約10分くらい」「約3時間ほど」のような「約」と「くらい/ぐらい/ほど」の二重表現
    [GeneratedRegex(@"約([0-9０-９一二三四五六七八九十百千万億兆.,、]+[^\s、。「」『』""]{0,4})(?:くらい|ぐらい|ほど)")]
    private static partial Regex ApproximateRedundancyRegex();

    // 直前が漢字・カタカナ・英数字のときだけ「をを」「がが」「でで」を 1 文字にする (「ででむし」「ががんぼ」を誤爆させない)
    [GeneratedRegex(@"(?<=[\p{IsCJKUnifiedIdeographs}\p{IsKatakana}0-9A-Za-z０-９Ａ-Ｚａ-ｚ々ヶ])([をがで])\1")]
    private static partial Regex DuplicateParticleRegex();

    /// <summary>文を推敲して書き換えがあれば返す。書き換えが無ければ null。isBlocked が true のパターンは適用しない。</summary>
    internal static ProofreadResult? Apply(string text, Func<string, bool>? isBlocked = null)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 2) return null;
        if (text.Contains("http://", StringComparison.OrdinalIgnoreCase) || text.Contains("https://", StringComparison.OrdinalIgnoreCase)) return null;
        if (text.All(c => c < 0x80)) return null;

        var output = new StringBuilder(text.Length);
        var reasons = new List<string>();
        var patterns = new List<string>();
        var pos = 0;

        foreach (var (start, length, quoted) in SplitQuotes(text))
        {
            var segment = text.Substring(start, length);
            if (quoted)
            {
                output.Append(segment);
            }
            else
            {
                output.Append(ApplyToSegment(segment, reasons, patterns, isBlocked));
            }
            pos = start + length;
        }
        if (pos < text.Length) output.Append(text, pos, text.Length - pos);

        var rewritten = output.ToString();
        return rewritten == text ? null : new ProofreadResult(rewritten, reasons, patterns);
    }

    private static string ApplyToSegment(string segment, List<string> reasons, List<string> patterns, Func<string, bool>? isBlocked)
    {
        var current = segment;
        foreach (var rule in ExactRules)
        {
            if (isBlocked?.Invoke(rule.Pattern) == true) continue;
            if (!current.Contains(rule.Pattern, StringComparison.Ordinal)) continue;
            current = current.Replace(rule.Pattern, rule.Replacement, StringComparison.Ordinal);
            if (!reasons.Contains(rule.Reason)) reasons.Add(rule.Reason);
            if (!patterns.Contains(rule.Pattern)) patterns.Add(rule.Pattern);
        }

        current = ApproximateRedundancyRegex().Replace(current, m =>
        {
            var key = "approx:" + m.Value;
            if (isBlocked?.Invoke(key) == true) return m.Value;
            if (!reasons.Contains("二重表現")) reasons.Add("二重表現");
            if (!patterns.Contains(key)) patterns.Add(key);
            return "約" + m.Groups[1].Value;
        });

        current = DuplicateParticleRegex().Replace(current, m =>
        {
            var key = "particle:" + m.Value;
            if (isBlocked?.Invoke(key) == true) return m.Value;
            if (!reasons.Contains("重複助詞")) reasons.Add("重複助詞");
            if (!patterns.Contains(key)) patterns.Add(key);
            return m.Groups[1].Value;
        });

        return current;
    }

    /// <summary>「…」「『…』」「"…"」の引用区間とそれ以外の区間に分ける。閉じられていない引用符は通常文として扱う。</summary>
    private static List<(int Start, int Length, bool Quoted)> SplitQuotes(string text)
    {
        var parts = new List<(int Start, int Length, bool Quoted)>();
        var plainStart = 0;
        var i = 0;
        while (i < text.Length)
        {
            var close = text[i] switch
            {
                '「' => '」',
                '『' => '』',
                '"' => '"',
                _ => '\0',
            };
            if (close == '\0')
            {
                i++;
                continue;
            }
            var end = text.IndexOf(close, i + 1);
            if (end < 0)
            {
                i++;
                continue;
            }
            if (i > plainStart) parts.Add((plainStart, i - plainStart, false));
            parts.Add((i, end - i + 1, true));
            i = end + 1;
            plainStart = i;
        }
        if (plainStart < text.Length) parts.Add((plainStart, text.Length - plainStart, false));
        return parts;
    }
}
