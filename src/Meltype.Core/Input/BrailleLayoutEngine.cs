// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 shiromenya

namespace Meltype.Input;

/// <summary>
/// 6点点字（パーキンス入力）の同時押しと日本語点字デコードを行う状態マシン。
/// F/D/S が左 1/2/3 の点、J/K/L が右 4/5/6 の点に対応し、最初にいずれかのキーが離された瞬間に 1 マスを確定する。
/// </summary>
internal sealed class BrailleLayoutEngine
{
    private enum Mode
    {
        None,
        Dakuten,
        Handakuten,
        Youon,
        YouDakuten,
        YouHandakuten,
        Numeric,
        Foreign,
    }

    private const int Dot1 = 1 << 0;
    private const int Dot2 = 1 << 1;
    private const int Dot3 = 1 << 2;
    private const int Dot4 = 1 << 3;
    private const int Dot5 = 1 << 4;
    private const int Dot6 = 1 << 5;

    private static readonly int NumSign = Dots(3, 4, 5, 6);
    private static readonly int ForeignSign = Dots(5, 6);
    private static readonly int CapitalSign = Dots(6);

    private static readonly Dictionary<int, string> BaseKana = new()
    {
        [Dots(1)] = "あ", [Dots(1, 2)] = "い", [Dots(1, 4)] = "う", [Dots(1, 2, 4)] = "え", [Dots(2, 4)] = "お",
        [Dots(1, 6)] = "か", [Dots(1, 2, 6)] = "き", [Dots(1, 4, 6)] = "く", [Dots(1, 2, 4, 6)] = "け", [Dots(2, 4, 6)] = "こ",
        [Dots(1, 5, 6)] = "さ", [Dots(1, 2, 5, 6)] = "し", [Dots(1, 4, 5, 6)] = "す", [Dots(1, 2, 4, 5, 6)] = "せ", [Dots(2, 4, 5, 6)] = "そ",
        [Dots(1, 3, 5)] = "た", [Dots(1, 2, 3, 5)] = "ち", [Dots(1, 3, 4, 5)] = "つ", [Dots(1, 2, 3, 4, 5)] = "て", [Dots(2, 3, 4, 5)] = "と",
        [Dots(1, 3)] = "な", [Dots(1, 2, 3)] = "に", [Dots(1, 3, 4)] = "ぬ", [Dots(1, 2, 3, 4)] = "ね", [Dots(2, 3, 4)] = "の",
        [Dots(1, 3, 6)] = "は", [Dots(1, 2, 3, 6)] = "ひ", [Dots(1, 3, 4, 6)] = "ふ", [Dots(1, 2, 3, 4, 6)] = "へ", [Dots(2, 3, 4, 6)] = "ほ",
        [Dots(1, 3, 5, 6)] = "ま", [Dots(1, 2, 3, 5, 6)] = "み", [Dots(1, 3, 4, 5, 6)] = "む", [Dots(1, 2, 3, 4, 5, 6)] = "め", [Dots(2, 3, 4, 5, 6)] = "も",
        [Dots(3, 4)] = "や", [Dots(3, 4, 6)] = "ゆ", [Dots(3, 4, 5)] = "よ",
        [Dots(1, 5)] = "ら", [Dots(1, 2, 5)] = "り", [Dots(1, 4, 5)] = "る", [Dots(1, 2, 4, 5)] = "れ", [Dots(2, 4, 5)] = "ろ",
        [Dots(3)] = "わ", [Dots(3, 5)] = "を", [Dots(3, 5, 6)] = "ん",
        [Dots(2)] = "っ", [Dots(2, 5)] = "ー",
        [Dots(2, 5, 6)] = "。", [Dots(2, 6)] = "？", [Dots(2, 3, 5)] = "！",
    };

    private static readonly Dictionary<string, string> DakutenMap = new(StringComparer.Ordinal)
    {
        ["か"] = "が", ["き"] = "ぎ", ["く"] = "ぐ", ["け"] = "げ", ["こ"] = "ご",
        ["さ"] = "ざ", ["し"] = "じ", ["す"] = "ず", ["せ"] = "ぜ", ["そ"] = "ぞ",
        ["た"] = "だ", ["ち"] = "ぢ", ["つ"] = "づ", ["て"] = "で", ["と"] = "ど",
        ["は"] = "ば", ["ひ"] = "び", ["ふ"] = "ぶ", ["へ"] = "べ", ["ほ"] = "ぼ",
        ["う"] = "ゔ",
    };

    private static readonly Dictionary<string, string> HandakutenMap = new(StringComparer.Ordinal)
    {
        ["は"] = "ぱ", ["ひ"] = "ぴ", ["ふ"] = "ぷ", ["へ"] = "ぺ", ["ほ"] = "ぽ",
    };

    private static readonly Dictionary<string, string> YouonMap = new(StringComparer.Ordinal)
    {
        ["か"] = "きゃ", ["く"] = "きゅ", ["こ"] = "きょ",
        ["さ"] = "しゃ", ["す"] = "しゅ", ["そ"] = "しょ", ["せ"] = "しぇ",
        ["た"] = "ちゃ", ["つ"] = "ちゅ", ["と"] = "ちょ", ["て"] = "ちぇ", ["ち"] = "てぃ",
        ["な"] = "にゃ", ["ぬ"] = "にゅ", ["の"] = "にょ",
        ["は"] = "ひゃ", ["ふ"] = "ひゅ", ["ほ"] = "ひょ",
        ["ま"] = "みゃ", ["む"] = "みゅ", ["も"] = "みょ",
        ["ら"] = "りゃ", ["る"] = "りゅ", ["ろ"] = "りょ",
    };

    private static readonly Dictionary<string, string> YouDakutenMap = new(StringComparer.Ordinal)
    {
        ["か"] = "ぎゃ", ["く"] = "ぎゅ", ["こ"] = "ぎょ",
        ["さ"] = "じゃ", ["す"] = "じゅ", ["そ"] = "じょ", ["せ"] = "じぇ",
        ["た"] = "ぢゃ", ["つ"] = "ぢゅ", ["と"] = "ぢょ", ["ち"] = "でぃ",
        ["は"] = "びゃ", ["ふ"] = "びゅ", ["ほ"] = "びょ",
        ["う"] = "ゔ",
    };

    private static readonly Dictionary<string, string> YouHandakutenMap = new(StringComparer.Ordinal)
    {
        ["は"] = "ぴゃ", ["ふ"] = "ぴゅ", ["ほ"] = "ぴょ",
    };

    private static readonly Dictionary<int, string> NumericMap = new()
    {
        [Dots(1)] = "1", [Dots(1, 2)] = "2", [Dots(1, 4)] = "3", [Dots(1, 4, 5)] = "4", [Dots(1, 5)] = "5",
        [Dots(1, 2, 4)] = "6", [Dots(1, 2, 4, 5)] = "7", [Dots(1, 2, 5)] = "8", [Dots(2, 4)] = "9", [Dots(2, 4, 5)] = "0",
        [Dots(2)] = ".", [Dots(3)] = ",",
    };

    private static readonly Dictionary<int, string> ForeignMap = new()
    {
        [Dots(1)] = "a", [Dots(1, 2)] = "b", [Dots(1, 4)] = "c", [Dots(1, 4, 5)] = "d", [Dots(1, 5)] = "e",
        [Dots(1, 2, 4)] = "f", [Dots(1, 2, 4, 5)] = "g", [Dots(1, 2, 5)] = "h", [Dots(2, 4)] = "i", [Dots(2, 4, 5)] = "j",
        [Dots(1, 3)] = "k", [Dots(1, 2, 3)] = "l", [Dots(1, 3, 4)] = "m", [Dots(1, 3, 4, 5)] = "n", [Dots(1, 3, 5)] = "o",
        [Dots(1, 2, 3, 4)] = "p", [Dots(1, 2, 3, 4, 5)] = "q", [Dots(1, 2, 3, 5)] = "r", [Dots(2, 3, 4)] = "s", [Dots(2, 3, 4, 5)] = "t",
        [Dots(1, 3, 6)] = "u", [Dots(1, 2, 3, 6)] = "v", [Dots(2, 4, 5, 6)] = "w", [Dots(1, 3, 4, 6)] = "x", [Dots(1, 3, 4, 5, 6)] = "y",
        [Dots(1, 3, 5, 6)] = "z",
        [Dots(2)] = ",", [Dots(2, 5, 6)] = ".", [Dots(3, 6)] = "-",
    };

    private int _pressed;
    private int _pendingMask;
    private bool _waitAllUp;
    private Mode _mode;
    private bool _foreignCapital;

    /// <summary>いずれかの点字キーを押したままか。</summary>
    public bool IsHolding => _pressed != 0;

    /// <summary>前置符号・数符・外字符などの入力途中状態か。</summary>
    public bool HasPrefix => _mode != Mode.None || _foreignCapital;

    /// <summary>指定した仮想キーが 6 点点字のキー（F/D/S/J/K/L）か。</summary>
    public static bool IsBrailleKey(int vk) => DotBit(vk) != 0;

    /// <summary>前置状態のみを取り消す（BackSpace 用）。取り消す状態があれば true。</summary>
    public bool CancelPrefix()
    {
        if (!HasPrefix) return false;
        _mode = Mode.None;
        _foreignCapital = false;
        return true;
    }

    /// <summary>状態を初期化する。</summary>
    public void Reset()
    {
        _pressed = 0;
        _pendingMask = 0;
        _waitAllUp = false;
        _mode = Mode.None;
        _foreignCapital = false;
    }

    /// <summary>キー押下を処理する。</summary>
    public KanaStroke Down(int vk, bool shift)
    {
        var bit = DotBit(vk);
        if (shift || bit == 0) return KanaStroke.NotHandled;
        if ((_pressed & bit) != 0) return KanaStroke.Consumed;

        _pressed |= bit;
        if (!_waitAllUp) _pendingMask |= bit;
        return KanaStroke.Consumed;
    }

    /// <summary>キー解放を処理し、最初のキーが離された瞬間に 1 マスを確定して返す。</summary>
    public KanaStroke Up(int vk)
    {
        var bit = DotBit(vk);
        if (bit == 0) return KanaStroke.NotHandled;

        _pressed &= ~bit;
        KanaStroke result = KanaStroke.Consumed;
        if (!_waitAllUp && _pendingMask != 0)
        {
            var mask = _pendingMask;
            _pendingMask = 0;
            _waitAllUp = _pressed != 0;
            var decoded = Decode(mask);
            if (decoded.Length > 0) result = new KanaStroke(true, 0, decoded);
        }
        if (_pressed == 0) _waitAllUp = false;
        return result;
    }

    private string Decode(int mask)
    {
        if (_mode == Mode.Numeric)
        {
            if (NumericMap.TryGetValue(mask, out var digit)) return digit;
            _mode = Mode.None;
        }
        else if (_mode == Mode.Foreign)
        {
            if (mask == ForeignSign)
            {
                _mode = Mode.None;
                _foreignCapital = false;
                return "、";
            }
            if (mask == CapitalSign)
            {
                _foreignCapital = true;
                return "";
            }
            if (ForeignMap.TryGetValue(mask, out var ch))
            {
                if (_foreignCapital)
                {
                    _foreignCapital = false;
                    return ch.ToUpperInvariant();
                }
                return ch;
            }
            _mode = Mode.None;
            _foreignCapital = false;
        }

        if (_mode != Mode.None)
        {
            var prevMode = _mode;
            _mode = Mode.None;
            if (BaseKana.TryGetValue(mask, out var baseChar))
            {
                var table = prevMode switch
                {
                    Mode.Dakuten => DakutenMap,
                    Mode.Handakuten => HandakutenMap,
                    Mode.Youon => YouonMap,
                    Mode.YouDakuten => YouDakutenMap,
                    Mode.YouHandakuten => YouHandakutenMap,
                    _ => null,
                };
                if (table is not null && table.TryGetValue(baseChar, out var combined))
                    return combined;
            }
            return "";
        }

        if (mask == Dots(5)) { _mode = Mode.Dakuten; return ""; }
        if (mask == Dots(6)) { _mode = Mode.Handakuten; return ""; }
        if (mask == Dots(4)) { _mode = Mode.Youon; return ""; }
        if (mask == Dots(4, 5)) { _mode = Mode.YouDakuten; return ""; }
        if (mask == Dots(4, 6)) { _mode = Mode.YouHandakuten; return ""; }
        if (mask == NumSign) { _mode = Mode.Numeric; return ""; }
        if (mask == ForeignSign) { _mode = Mode.Foreign; _foreignCapital = false; return ""; }

        return BaseKana.GetValueOrDefault(mask, "");
    }

    private static int DotBit(int vk) => vk switch
    {
        0x46 => Dot1, // F
        0x44 => Dot2, // D
        0x53 => Dot3, // S
        0x4A => Dot4, // J
        0x4B => Dot5, // K
        0x4C => Dot6, // L
        _ => 0,
    };

    private static int Dots(params int[] dots)
    {
        var mask = 0;
        foreach (var d in dots) mask |= 1 << (d - 1);
        return mask;
    }
}
