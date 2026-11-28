// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 shiromenya

using Meltype.Composition;
using Meltype.Config;
using Meltype.Dictionary;
using Meltype.Engine;
using Meltype.Ime;
using Meltype.Input;
using static Meltype.Tests.CompositionTests;

namespace Meltype.Tests;

/// <summary>6点点字（パーキンス入力）のテスト。</summary>
internal static class BrailleLayoutTests
{
    private static readonly int[] DotVks = [0x46, 0x44, 0x53, 0x4A, 0x4B, 0x4C]; // F, D, S, J, K, L

    private static Keyboard Braille() => new() { Kana = true, KanaLayout = KanaLayout.Braille };

    private static void Chord(Keyboard k, params int[] dots)
    {
        foreach (var d in dots) k.Key(DotVks[d - 1]);
        foreach (var d in dots) k.Key(DotVks[d - 1], up: true);
    }

    [Test]
    public static void Braille_BasicKanaDakutenAndYouon()
    {
        var k = Braille();
        // あ(1) い(1,2) う(1,4) え(1,2,4) お(2,4)
        Chord(k, 1);
        Chord(k, 1, 2);
        Chord(k, 1, 4);
        Chord(k, 1, 2, 4);
        Chord(k, 2, 4);
        Assert.Equal("あいうえお", k.Showing);

        // が(5 + 1,6) ぱ(6 + 1,3,6) きゃ(4 + 1,6) じゅ(4,5 + 1,4,5,6) ぴょ(4,6 + 2,3,4,6)
        k = Braille();
        Chord(k, 5); Chord(k, 1, 6);
        Chord(k, 6); Chord(k, 1, 3, 6);
        Chord(k, 4); Chord(k, 1, 6);
        Chord(k, 4, 5); Chord(k, 1, 4, 5, 6);
        Chord(k, 4, 6); Chord(k, 2, 3, 4, 6);
        Assert.Equal("がぱきゃじゅぴょ", k.Showing);

        // っ(2) ー(2,5) ん(3,5,6) 。(2,5,6) ！(2,3,5) ？(2,6)
        k = Braille();
        Chord(k, 1, 6);
        Chord(k, 2);
        Chord(k, 2, 4, 6);
        Chord(k, 2, 5);
        Chord(k, 3, 5, 6);
        Chord(k, 2, 5, 6);
        Chord(k, 2, 3, 5);
        Chord(k, 2, 6);
        Assert.Equal("かっこーん。！？", k.Showing);
    }

    [Test]
    public static void Braille_ReleaseOrderDoesNotDuplicateCell()
    {
        var k = Braille();
        // 1,6,4 を押して、4 → 1 → 6 の順に離しても「く」が 1 文字だけ入る
        k.Key(DotVks[0]); // 1
        k.Key(DotVks[5]); // 6
        k.Key(DotVks[3]); // 4
        k.Key(DotVks[3], up: true);
        k.Key(DotVks[0], up: true);
        k.Key(DotVks[5], up: true);
        Assert.Equal("く", k.Showing);
    }

    [Test]
    public static void Braille_NumericAndForeignModes()
    {
        var k = Braille();
        // 数符(3,4,5,6) + 2(1,2) + 0(2,4,5) + 2(1,2) + 6(1,2,4) + ね(1,2,3,4 ※数符外なので解除して「ね」)
        Chord(k, 3, 4, 5, 6);
        Chord(k, 1, 2);
        Chord(k, 2, 4, 5);
        Chord(k, 1, 2);
        Chord(k, 1, 2, 4);
        Chord(k, 1, 2, 3, 4);
        Assert.Equal("2026ね", k.Showing);

        // 外字符(5,6) + 大文字符(6) + H(1,2,5) + i(2,4) + 再度(5,6 → 、)
        k = Braille();
        Chord(k, 5, 6);
        Chord(k, 6);
        Chord(k, 1, 2, 5);
        Chord(k, 2, 4);
        Chord(k, 5, 6);
        Assert.Equal("Hi、", k.Showing);
    }

    [Test]
    public static void Braille_BackspaceCancelsPrefixWithoutDeletingPreviousKana()
    {
        var k = Braille();
        Chord(k, 1); // あ
        Chord(k, 5); // 濁音符
        k.Press(VirtualKeys.Back); // 濁音符のみ取り消し
        Chord(k, 1, 6); // か (が ではなく か)
        Assert.Equal("あか", k.Showing);
    }

    [Test]
    public static void Braille_ConvertsInputToKanjiAndEnablesKeyReleasesInImeSession()
    {
        var k = Braille();
        // に(1,2,3) ほ(2,3,4,6) ん(3,5,6) ご(5 + 2,4,6)
        Chord(k, 1, 2, 3);
        Chord(k, 2, 3, 4, 6);
        Chord(k, 3, 5, 6);
        Chord(k, 5); Chord(k, 2, 4, 6);
        Assert.Equal("にほんご", k.Showing);
        k.Press(VirtualKeys.Space);
        Assert.True(k.Host.View is { Converting: true }, "6点点字で入力したかなを Space で変換できる");

        // ImeHost の Capture.KeyReleases が true になり、押下＋解放で入力できる
        var services = new CompositionServices();
        MeltypeRuntime.Connect(Detector, services);
        var host = new ImeHost(new BrailleEnv(), new SessionParts(Detector, new FakeConverter(), services),
            () => new Settings { InputStyle = InputStyle.Kana, KanaLayout = KanaLayout.Braille });
        var init = host.Handle(new ImeEvent.StateRequested());
        Assert.True(init.State.Capture.KeyReleases, "6点点字では KeyReleases が有効になる");

        var down1 = host.Handle(new ImeEvent.KeyPressed(new ImeKey { Vk = 0x46, Ch = 'f', ReleaseFollows = true }));
        Assert.True(down1.Result.Consumed, "1の点押下は消費される");
        var up1 = host.Handle(new ImeEvent.KeyReleased(new ImeKeyRelease(0x46)));
        Assert.Equal("あ", up1.Result.View?.Text);
    }

    [Test]
    public static void CandidateAnnouncement_FormatsCandidateNoteMeaningAndPrediction()
    {
        var candidateView = new CompositionView(
            Text: "にほんご",
            Candidates: ["日本語", "二本後"],
            SelectedIndex: 0,
            Converting: true,
            Hint: "",
            Notes: [CandidateNote.Zenzai, CandidateNote.None],
            Meaning: "日本の国語");
        Assert.Equal("候補 (1/2): 日本語、Zenzai、日本の国語", CandidateAnnouncement.Format(candidateView));

        var predictionView = new CompositionView(
            Text: "にほ",
            Candidates: [],
            SelectedIndex: -1,
            Converting: false,
            Hint: "",
            Predictions: ["日本語", "日本"],
            SelectedPrediction: 1);
        Assert.Equal("予測候補 (2/2): 日本", CandidateAnnouncement.Format(predictionView));

        var typingView = new CompositionView(
            Text: "にほんご",
            Candidates: [],
            SelectedIndex: -1,
            Converting: false,
            Hint: "");
        Assert.Equal(null, CandidateAnnouncement.Format(typingView));
    }

    private sealed class BrailleEnv : IImeEnvironment
    {
        public InputMode? InputMethod => null;
        public SnippetStore Snippets { get; } = new(null);
        public string? ClipboardText => null;
        public string? OtherTipVersion => null;
    }
}
