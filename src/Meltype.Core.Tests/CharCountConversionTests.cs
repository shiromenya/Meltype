// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 shiromenya

using Meltype.Composition;
using Meltype.Composition.Utility;
using Meltype.Config;
using Meltype.Engine;
using Meltype.Input;
using static Meltype.Tests.CompositionTests;

namespace Meltype.Tests;

internal static class CharCountConversionTests
{
    [Test]
    public static void CharCount_CountsBeforeTextWithSpacesNewlinesAndEmoji()
    {
        // 日本語 + 半角/全角スペース + 改行 + ZWJ 絵文字 (1 書記素クラスタ)
        var text = "こんにちは 　世界\r\n👨‍👩‍👧‍👦!";
        var (total, noSpace, lines) = CharCountConversion.Count(text);
        Assert.Equal(11, total, "改行を除き書記素クラスタ単位で 11 文字");
        Assert.Equal(9, noSpace, "半角・全角スペースを除くと 9 文字");
        Assert.Equal(2, lines, "改行により 2 行");

        var k = new Keyboard();
        k.Host.PrecedingText = text;
        k.Type("mozisuu ");
        var candidates = k.Host.View!.Candidates;
        Assert.True(candidates.Contains("11文字"), "総文字数候補が出る: " + string.Join(",", candidates));
        Assert.True(candidates.Contains("9文字 (空白除く)"), "空白除く候補が出る: " + string.Join(",", candidates));
        Assert.True(candidates.Contains("11文字 (2行)"), "行数付き候補が出る: " + string.Join(",", candidates));
        Assert.True(candidates.Contains("11"), "数字のみ候補が出る: " + string.Join(",", candidates));

        // 別の読みでは出ない
        var other = new Keyboard();
        other.Host.PrecedingText = text;
        other.Type("kaunto ");
        Assert.True(!other.Host.View!.Candidates.Contains("11文字"), "もじすう以外では出さない");
    }

    [Test]
    public static void CharCount_PrefersSelectionHandlesEmptyAndNullAndDoesNotLearn()
    {
        var reading = new TextReading(new CompositionTextState());

        // Selection があれば Before より優先する
        var input = new UtilityInput(
            reading,
            "もじすう",
            Direct: false,
            new UtilityContext(new Settings(), DateTime.Now, () => null, () => null, null, () => null, Before: "あいうえお", Selection: "かき"));
        var converted = new CharCountConversion().Convert(input);
        Assert.True(converted is not null && converted.Candidates[0] == "2文字", "選択範囲の文字数を優先");
        Assert.True(converted!.NotLearned.Contains("2文字"), "毎回値が変わるため学習しない");

        // Before が空文字なら 0文字
        var empty = new UtilityInput(
            reading,
            "もじすう",
            Direct: false,
            new UtilityContext(new Settings(), DateTime.Now, () => null, () => null, null, () => null, Before: ""));
        var emptyRes = new CharCountConversion().Convert(empty);
        Assert.True(emptyRes is not null && emptyRes.Candidates[0] == "0文字", "空文字は 0文字");

        // Before も Selection も null (周辺文字列を読めない欄) なら出さない
        var unreadable = new UtilityInput(
            reading,
            "もじすう",
            Direct: false,
            new UtilityContext(new Settings(), DateTime.Now, () => null, () => null, null, () => null));
        Assert.True(new CharCountConversion().Convert(unreadable) is null, "読めない欄では候補を出さない");

        // Before が 4000 文字以上なら上限到達が分かる候補を出す
        var longBefore = new string('あ', 4000);
        var capped = new UtilityInput(
            reading,
            "もじすう",
            Direct: false,
            new UtilityContext(new Settings(), DateTime.Now, () => null, () => null, null, () => null, Before: longBefore));
        var cappedRes = new CharCountConversion().Convert(capped);
        Assert.True(cappedRes is not null && cappedRes.Candidates[0] == "4000文字以上 (直前4000字上限)", "4000字上限を明示する");
    }
}
