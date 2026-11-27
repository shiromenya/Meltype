// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 shiromenya

using System.Globalization;
using System.Text.RegularExpressions;

namespace Meltype.Composition.Utility;

/// <summary>
/// 進数変換のユーティリティ変換 (2〜36 進数、学習対象外):
/// - 数字のみ (255) → 代表 3 進数 (2・8・16 進数) を漢数字の後ろに追加
/// - 読み絞り込み (255を7しんすう / 255を16しんすうに) → 指定した進数を先頭に追加
/// - 関数形式 (base(255,16) / base(ff,16,2) / base(ff,16,10)) → 原文から m進数→n進数 変換し第一候補に
/// - リテラル形式 (0xff / 0b1010 / 0o377) → Space の英数字確定は維持しつつ、変換キーや予測候補で 10 進数等に変換
/// </summary>
internal sealed partial class BaseConversion : IUtilityConversion
{
    private const string BaseDigits = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private static readonly int[] RepresentativeBases = [2, 8, 16];

    [GeneratedRegex(@"^(?<n>[0-9０-９]{1,16})を(?<base>[0-9０-９]{1,2})しんすう(?:に)?$")]
    private static partial Regex ReadingTargetBase();

    public UtilityCandidates? Convert(UtilityInput input)
    {
        var raw = CompositionText.Raw(input.Text);
        if (!input.Text.KanaInput && input.Text.Mode == DisplayMode.Auto)
        {
            if (FunctionCandidates(raw) is { Count: > 0 } funcCandidates)
            {
                var results = new List<string>(funcCandidates);
                if (!results.Contains(raw)) results.Add(raw);
                return new(results, UtilityPlacement.Replace);
            }
            if (LiteralCandidates(raw) is { Count: > 0 } litCandidates)
                return new([raw, .. litCandidates], UtilityPlacement.Replace);
        }

        if (input.Reading is not { } reading) return null;

        if (ReadingTargetBase().Match(reading) is { Success: true } targetMatch)
        {
            var digits = NormalizeDigits(targetMatch.Groups["n"].Value);
            var targetBase = UtilityConversions.Number(targetMatch.Groups["base"].Value);
            if (targetBase is >= 2 and <= 36 && targetBase != 10 &&
                !(digits.Length > 1 && digits[0] == '0') &&
                ulong.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                return new(FormatTargetCandidates(value, targetBase), UtilityPlacement.Prepend);
            }
        }

        if (DigitsCandidates(reading) is { Count: > 0 } digitCandidates)
            return new(digitCandidates, UtilityPlacement.Append);

        return null;
    }

    /// <summary>原文全体が有効な base(...) 関数呼び出しに一致するか (Space 1 回で変換を開始する判定用)。</summary>
    internal static bool MatchesFunction(string raw) => FunctionCandidates(raw).Count > 0;

    /// <summary>入力中の予測候補に出す変換結果 (base(...) と 0x/0b/0o リテラル)。</summary>
    internal static List<string> PredictionCandidates(string raw)
    {
        var func = FunctionCandidates(raw);
        if (func.Count > 0) return func;
        return LiteralCandidates(raw);
    }

    /// <summary>数字のみの入力 (255) に対する代表 3 進数 (2・8・16 進数)。</summary>
    internal static List<string> DigitsCandidates(string text)
    {
        var half = NormalizeDigits(text.Replace(",", ""));
        // 先頭が 0 の複数桁 (001 など) や 17 桁以上は対象外にする
        if (half.Length is 0 or > 16 || !half.All(char.IsAsciiDigit) || (half.Length > 1 && half[0] == '0'))
            return [];

        var value = ulong.Parse(half, CultureInfo.InvariantCulture);
        if (value < 2) return [];

        return RepresentativeBases.Select(radix => $"{FormatBase(value, radix)}({radix}進数)").ToList();
    }

    /// <summary>関数形式: base(値, n) は 10→n進数、base(値, m, n) は m→n進数。</summary>
    internal static List<string> FunctionCandidates(string raw)
    {
        if (!TrySplitBaseCall(raw, out var argsText)) return [];
        var parts = argsText.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length is not (2 or 3)) return [];

        int fromBase;
        int toBase;
        if (parts.Length == 2)
        {
            fromBase = 10;
            if (!TryParseBase(parts[1], out toBase)) return [];
        }
        else
        {
            if (!TryParseBase(parts[1], out fromBase) || !TryParseBase(parts[2], out toBase)) return [];
        }

        if (fromBase == toBase) return [];
        if (!TryParseInBase(parts[0], fromBase, out var value)) return [];
        return FormatTargetCandidates(value, toBase);
    }

    /// <summary>リテラル形式: 0b... / 0o... / 0x... を 10 進数と他の代表進数に変換する。</summary>
    internal static List<string> LiteralCandidates(string raw)
    {
        if (raw.Length < 3 || raw[0] != '0') return [];
        var fromBase = raw[1] switch
        {
            'b' or 'B' => 2,
            'o' or 'O' => 8,
            'x' or 'X' => 16,
            _ => 0,
        };
        if (fromBase == 0) return [];
        if (!TryParseInBase(raw[2..], fromBase, out var value)) return [];

        var results = new List<string> { $"{value}(10進数)" };
        foreach (var radix in RepresentativeBases)
        {
            if (radix == fromBase) continue;
            results.Add($"{FormatBase(value, radix)}({radix}進数)");
        }
        return results;
    }

    private static List<string> FormatTargetCandidates(ulong value, int toBase)
    {
        var formatted = FormatBase(value, toBase);
        var results = new List<string> { formatted };
        var prefix = toBase switch
        {
            2 => "0b",
            8 => "0o",
            16 => "0x",
            _ => null,
        };
        if (prefix is not null) results.Add(prefix + formatted);
        results.Add($"{formatted}({toBase}進数)");
        return results;
    }

    private static bool TrySplitBaseCall(string raw, out string args)
    {
        args = "";
        if (raw.Length < 7 || !raw.EndsWith(')')) return false;
        var open = raw.IndexOf('(');
        if (open != 4) return false;
        if (!raw.AsSpan(0, 4).Equals("base", StringComparison.OrdinalIgnoreCase)) return false;
        args = raw[5..^1].Trim();
        return args.Length > 0;
    }

    private static bool TryParseBase(string text, out int radix) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out radix) && radix is >= 2 and <= 36;

    private static bool TryParseInBase(string text, int radix, out ulong value)
    {
        value = 0;
        if (text.Length == 0) return false;
        var baseVal = (ulong)radix;
        foreach (var c in text)
        {
            var digit = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'a' and <= 'z' => c - 'a' + 10,
                >= 'A' and <= 'Z' => c - 'A' + 10,
                _ => -1,
            };
            if (digit < 0 || digit >= radix) return false;
            var d = (ulong)digit;
            if (value > (ulong.MaxValue - d) / baseVal) return false;
            value = value * baseVal + d;
        }
        return true;
    }

    private static string NormalizeDigits(string text) =>
        string.Concat(text.Select(c => c is >= '０' and <= '９' ? (char)(c - '０' + '0') : c));

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
