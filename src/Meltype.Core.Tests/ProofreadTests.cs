// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 shiromenya

using Meltype.Composition;
using Meltype.Config;
using Meltype.Dictionary;
using Meltype.Engine;
using Meltype.Input;
using Meltype.Learning;
using static Meltype.Tests.CompositionTests;

namespace Meltype.Tests;

internal static class ProofreadTests
{
    private sealed class MapConverter(Dictionary<string, ConversionClause[]> clauses) : TestConverter
    {
        private readonly FakeConverter _inner = new();
        public override string? Convert(string hiragana, string? before) =>
            ConvertClauses(hiragana, before) is { } parts ? string.Concat(parts.Select(c => c.Text)) : _inner.Convert(hiragana, before);
        public override IReadOnlyList<ConversionClause>? ConvertClauses(string hiragana, string? before) =>
            clauses.TryGetValue(hiragana, out var result) ? result : _inner.ConvertClauses(hiragana, before);
    }

    [Test]
    public static void Proofread_DefaultSettingIsOff()
    {
        Assert.True(!new Settings().ProofreadMode, "文章推敲は既定で OFF");
    }

    [Test]
    public static void Proofread_RewritesRedundancyRaNukiAndTypos()
    {
        foreach (var (input, expected, reason) in new[]
        {
            ("確認することができます", "確認できます", "冗長表現"),
            ("保存することができました", "保存できました", "冗長表現"),
            ("まず最初に確認します", "最初に確認します", "二重表現"),
            ("約10分くらいかかります", "約10分かかります", "二重表現"),
            ("頭痛が痛いので休みます", "頭が痛いので休みます", "二重表現"),
            ("映画が見れる", "映画が見られる", "ら抜き言葉"),
            ("早く起きれます", "早く起きられます", "ら抜き言葉"),
            ("的を得た意見", "的を射た意見", "誤用"),
            ("汚名挽回したい", "汚名返上したい", "誤用"),
            ("資料をを確認します", "資料を確認します", "重複助詞"),
        })
        {
            var result = Proofread.Apply(input);
            Assert.True(result is not null, $"{input} が推敲されなかった");
            Assert.Equal(expected, result!.Text, input);
            Assert.True(result.Reasons.Contains(reason), $"{input} の理由に {reason}: {string.Join(",", result.Reasons)}");
        }
    }

    [Test]
    public static void Proofread_ProtectsQuotesUrlsAndAscii()
    {
        Assert.True(Proofread.Apply("「見れる」と書いてあった") is null, "鍵括弧の引用内は書き換えない");
        Assert.True(Proofread.Apply("『まず最初に』が題名") is null, "二重鍵括弧の引用内は書き換えない");
        Assert.True(Proofread.Apply("\"見れる\" のまま") is null, "ダブルクォートの引用内は書き換えない");
        Assert.True(Proofread.Apply("https://example.com/見れる") is null, "URL を含む文は書き換えない");
        Assert.True(Proofread.Apply("hello world") is null, "英字だけの文は書き換えない");
        Assert.True(Proofread.Apply("ででむしを見つけた") is null, "ひらがなの語に含まれる でで は重複助詞にしない");
        Assert.True(Proofread.Apply("ががんぼが飛ぶ") is null, "ひらがなの語に含まれる がが は重複助詞にしない");

        var particleAfterKanji = Proofread.Apply("東京でで会う");
        Assert.True(particleAfterKanji is not null, "漢字の後ろの重複助詞は直す");
        Assert.Equal("東京で会う", particleAfterKanji!.Text);

        var mixed = Proofread.Apply("「見れる」は確認することができます");
        Assert.True(mixed is not null, "引用の外側は推敲する");
        Assert.Equal("「見れる」は確認できます", mixed!.Text);
    }

    [Test]
    public static void Proofread_AppliesMultipleRulesInOneSentence()
    {
        var result = Proofread.Apply("まず最初に映画が見れることを確認することができます");
        Assert.True(result is not null, "複数ルールの推敲結果が返る");
        Assert.Equal("最初に映画が見られることを確認できます", result!.Text);
    }

    [Test]
    public static void Proofread_OffDoesNotChangeConversion()
    {
        var converter = new MapConverter(new()
        {
            ["かくにんすることができます"] = [new("かくにん", "確認"), new("することができます", "することができます")],
        });
        var k = new Keyboard(converter: converter);
        k.Settings.ProofreadMode = false;
        k.Type("kakuninsurukotogadekimasu ");
        Assert.Equal("確認することができます", k.Host.View!.Text);
    }

    [Test]
    public static void Proofread_OnPrependsCandidateAndSpaceRevertsToOriginal()
    {
        var converter = new MapConverter(new()
        {
            ["かくにんすることができます"] = [new("かくにん", "確認"), new("することができます", "することができます")],
        });
        var history = new ConversionHistory(null);
        var k = new Keyboard(converter: converter, history: history);
        k.Settings.ProofreadMode = true;
        k.Type("kakuninsurukotogadekimasu ");

        var view = k.Host.View!;
        Assert.Equal("確認できます", view.Text, "推敲後の文が第 1 候補");
        Assert.Equal("確認できます", view.Candidates[0]);
        Assert.Equal("確認することができます", view.Candidates[1], "元の表現が第 2 候補に残る");
        Assert.Equal(CandidateNote.Proofread, view.Notes![0], "推敲の注記が付く");
        Assert.Equal("推敲", view.ContextHint, "コンテキストヒントに推敲と出る");
        Assert.Equal("文章推敲: 冗長表現", view.Meaning, "推敲理由が意味欄に出る");

        // Space で元の表現に戻せる
        k.Press(VirtualKeys.Space);
        Assert.Equal("確認することができます", k.Host.View!.Text, "Space で元の表現に戻せる");
        Assert.True(k.Host.View.ContextHint is null, "元の表現を選んでいるときは推敲ヒントを消す");
        Assert.True(k.Host.View.Meaning is null, "元の表現を選んでいるときは推敲理由を消す");
    }

    [Test]
    public static void Proofread_KeepsZenzaiBadgeWhenJoiningMultipleClauses()
    {
        var converter = new MapConverter(new()
        {
            ["かくにんすることができます"] = [new("かくにん", "確認") { Zenzai = true }, new("することができます", "することができます") { Zenzai = true }],
        });
        var k = new Keyboard(converter: converter);
        k.Settings.ProofreadMode = true;
        k.Type("kakuninsurukotogadekimasu ");

        var view = k.Host.View!;
        Assert.Equal("確認できます", view.Text);
        Assert.Equal("Zenzai · 推敲", view.ContextHint, "Zenzai 由来の推敲では両方のバッジを出す");
        Assert.Equal(CandidateNote.Proofread, view.Notes![0]);
        Assert.Equal(CandidateNote.Zenzai, view.Notes![1], "元の表現に Zenzai 注記が残る");
    }

    [Test]
    public static void Proofread_LiveConversionPreservesPunctuationStyle()
    {
        var converter = new MapConverter(new()
        {
            ["まずさいしょに、かくにんすることができます。"] =
            [
                new("まず", "まず"),
                new("さいしょに", "最初に"),
                new("、", "、"),
                new("かくにん", "確認"),
                new("することができます", "することができます"),
                new("。", "。"),
            ],
        });
        var k = new Keyboard(converter: converter);
        k.Settings.LiveConversion = true;
        k.Settings.ProofreadMode = true;
        k.Settings.Punctuation = PunctuationStyle.FullWidthCommaPeriod;
        k.Type("mazusaisyoni,kakuninsurukotogadekimasu.");

        Assert.Equal("最初に，確認できます．", k.Host.View!.Text, "ライブ変換の推敲でも句読点スタイル (，．) を保つ");
        k.Press(VirtualKeys.Return);
        Assert.Equal("最初に，確認できます．", k.Host.Document);
    }

    [Test]
    public static void Proofread_ReconversionProofreadsSelectionDirectly()
    {
        var k = new Keyboard();
        k.Settings.ProofreadMode = true;
        k.Host.Selection = new ReconversionSelection("特殊な漢字も確認することができます", "とくしゅなかんじもかくにんすることができます");
        k.Press(VirtualKeys.Convert);

        var view = k.Host.View!;
        Assert.Equal("特殊な漢字も確認できます", view.Text, "選択テキストの漢字を崩さず推敲する");
        Assert.Equal("特殊な漢字も確認することができます", view.Candidates[1], "元の選択テキストが第 2 候補に残る");
        Assert.Equal(CandidateNote.Proofread, view.Notes![0]);
        Assert.Equal("文章推敲: 冗長表現", view.Meaning);
        k.Press(VirtualKeys.Return);
        Assert.Equal("特殊な漢字も確認できます", k.Host.Document);
    }

    [Test]
    public static void Proofread_AcceptedCandidateIsNotLearned()
    {
        var converter = new MapConverter(new()
        {
            ["みれる"] = [new("みれる", "見れる")],
        });
        var history = new ConversionHistory(null);
        var k = new Keyboard(converter: converter, history: history);
        k.Settings.ProofreadMode = true;
        k.Type("mireru ");
        Assert.Equal("見られる", k.Host.View!.Text);
        k.Press(VirtualKeys.Return);

        Assert.Equal("見られる", k.Host.Document);
        Assert.True(history.Get("みれる") is null, "推敲候補を確定しても読みの学習に入れない");
    }

    [Test]
    public static void Proofread_WorksWithLiveConversion()
    {
        var converter = new MapConverter(new()
        {
            ["まずさいしょに"] = [new("まず", "まず"), new("さいしょに", "最初に")],
        });
        var k = new Keyboard(converter: converter);
        k.Settings.LiveConversion = true;
        k.Settings.ProofreadMode = true;
        k.Type("mazusaisyoni");

        Assert.Equal("最初に", k.Host.View!.Text, "ライブ変換中も推敲結果を表示する");
        k.Press(VirtualKeys.Return);
        Assert.Equal("最初に", k.Host.Document, "ライブ変換からそのまま確定しても推敲結果が入る");
    }

    [Test]
    public static void Proofread_AutoBlocksRejectedPatternAndRecoversWhenAcceptedAgain()
    {
        var converter = new MapConverter(new()
        {
            ["みれる"] = [new("みれる", "見れる")],
        });
        var k = new Keyboard(converter: converter);
        k.Settings.ProofreadMode = true;

        // 1 回目: 推敲候補を Space で原文に戻して確定 (却下を記録)
        k.Type("mireru ");
        Assert.Equal("見られる", k.Host.View!.Text);
        k.Press(VirtualKeys.Space);
        Assert.Equal("見れる", k.Host.View.Text);
        k.Press(VirtualKeys.Return);

        // 2 回目: 自動抑止中は元の表現が第 1 候補、ライブ変換でも書き換えない。第 2 候補の推敲案を選び直して確定すると自己回復する
        k.Settings.LiveConversion = true;
        k.Type("mireru");
        Assert.Equal("見れる", k.Host.View!.Text, "自動抑止中はライブ変換でも推敲しない");
        k.Press(VirtualKeys.Space);
        Assert.Equal("見れる", k.Host.View!.Text, "自動抑止中は元の表現が第 1 候補");
        Assert.Equal("見られる", k.Host.View.Candidates[1], "推敲案は第 2 候補に残る");
        k.Press(VirtualKeys.Space);
        Assert.Equal("見られる", k.Host.View.Text);
        k.Press(VirtualKeys.Return);

        // 3 回目: 採用が追いついたので自動抑止が解除される
        k.Settings.LiveConversion = false;
        k.Type("mireru ");
        Assert.Equal("見られる", k.Host.View!.Text, "再度採用すると自動抑止が解除される");
    }

    [Test]
    public static void Proofread_DoesNotRecordFeedbackWhenPrivateOrLearningDisabled()
    {
        var converter = new MapConverter(new()
        {
            ["みれる"] = [new("みれる", "見れる")],
        });

        var k1 = new Keyboard(converter: converter);
        k1.Settings.ProofreadMode = true;
        k1.Learning = false;
        k1.Type("mireru ");
        k1.Press(VirtualKeys.Space);
        k1.Press(VirtualKeys.Return);
        k1.Type("mireru ");
        Assert.Equal("見られる", k1.Host.View!.Text, "学習 OFF のときは却下を記録しない");

        var k2 = new Keyboard(converter: converter);
        k2.Settings.ProofreadMode = true;
        k2.Private = true;
        k2.Type("mireru ");
        k2.Press(VirtualKeys.Space);
        k2.Press(VirtualKeys.Return);
        k2.Type("mireru ");
        Assert.Equal("見られる", k2.Host.View!.Text, "プライベート入力欄では却下を記録しない");
    }

    [Test]
    public static void Proofread_FixesPrecedingContextDuplicates()
    {
        var particle = Proofread.Apply("を確認する", precedingText: "資料を");
        Assert.True(particle is not null, "確定済みの直前文との重複助詞を直す");
        Assert.Equal("確認する", particle!.Text);
        Assert.True(particle.Reasons.Contains("重複助詞"), string.Join(",", particle.Reasons));

        var approx = Proofread.Apply("くらいかかります", precedingText: "約10分");
        Assert.True(approx is not null, "確定済みの 約10分 と くらい の二重表現を直す");
        Assert.Equal("かかります", approx!.Text);

        var first = Proofread.Apply("最初に確認します", precedingText: "まず");
        Assert.True(first is not null, "確定済みの まず と 最初に の二重表現を直す");
        Assert.Equal("確認します", first!.Text);

        var majority = Proofread.Apply("以上が賛成", precedingText: "過半数");
        Assert.True(majority is not null, "確定済みの 過半数 と 以上 の二重表現を直す");
        Assert.Equal("が賛成", majority!.Text);

        Assert.True(Proofread.Apply("を", precedingText: "資料を") is null, "削ると空になる 1 文字は対象外");

        var converter = new MapConverter(new()
        {
            ["しりょうを"] = [new("しりょうを", "資料を")],
            ["をかくにんする"] = [new("を", "を"), new("かくにんする", "確認する")],
        });
        var k = new Keyboard(converter: converter);
        k.Settings.ProofreadMode = true;
        k.Type("siryouwo \n");
        k.Type("wokakuninsuru ");
        Assert.Equal("確認する", k.Host.View!.Text, "直前文脈の重複助詞を削った候補が先頭");
        Assert.Equal("を確認する", k.Host.View.Candidates[1], "元の表現が第 2 候補に残る");
        k.Press(VirtualKeys.Space);
        Assert.Equal("を確認する", k.Host.View.Text, "Space で元の表現に戻せる");
    }

    [Test]
    public static void Zenzai_ProtectsTypedDigitsAndRejectsUnexpectedEmojis()
    {
        Assert.True(ZenzaiConverter.KeepsEmojis("えがお", "笑顔"), "通常の漢字変換は通す");
        Assert.True(!ZenzaiConverter.KeepsEmojis("えがお", "😊"), "読みに無い絵文字への置換は弾く");
        Assert.True(!ZenzaiConverter.KeepsEmojis("ありがとう", "ありがとう😊"), "末尾に絵文字を足す答えは弾く");
        Assert.True(ZenzaiConverter.KeepsEmojis("しかる", "𠮟る"), "CJK 拡張漢字 (サロゲートペア) は絵文字と誤認しない");

        Assert.True(ZenzaiConverter.KeepsTypedDigits("2026ねん", "2026年"), "打った数字を保つ答えは通す");
        Assert.True(!ZenzaiConverter.KeepsTypedDigits("2026ねん", "2025年"), "打った数字を別数字に変える答えは弾く");
        Assert.True(ZenzaiConverter.KeepsTypedDigits("にせんにじゅうろくねん", "2026年"), "読みに数字が無い場合の数字変換は通す");

        var mozc = new List<(ConversionClause Clause, IReadOnlyList<string> Candidates)>
        {
            (new("2026ねん", "2026年"), ["2026年"]),
        };
        var chosenDigit = HybridConverter.Choose("2026ねん", mozc, [new("2026ねん", "2025年") { Zenzai = true }]);
        Assert.Equal("2026年", chosenDigit![0].Text, "数字を改変した Zenzai の答えより Mozc を使う");

        var mozcEmoji = new List<(ConversionClause Clause, IReadOnlyList<string> Candidates)>
        {
            (new("えがお", "笑顔"), ["笑顔", "😊"]),
        };
        var chosenEmoji = HybridConverter.Choose("えがお", mozcEmoji, [new("えがお", "😊") { Zenzai = true }]);
        Assert.Equal("笑顔", chosenEmoji![0].Text, "読みに無い絵文字を出した Zenzai の答えより Mozc を使う");
    }
}
