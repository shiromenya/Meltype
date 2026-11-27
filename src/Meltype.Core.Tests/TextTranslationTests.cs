// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 shiromenya

using Meltype.Composition;
using Meltype.Config;
using Meltype.Dictionary;
using Meltype.Engine;
using Meltype.Ime;
using Meltype.Input;
using Meltype.Learning;
using static Meltype.Tests.CompositionTests;

namespace Meltype.Tests;

internal static class TextTranslationTests
{
    private sealed class FakeTranslator : ITextTranslator
    {
        public Dictionary<(TranslationTarget Target, string Text), string> Results { get; } = new();
        public HashSet<(TranslationTarget Target, string Text)> Pending { get; } = [];
        public List<(TranslationTarget Target, string Text, string ModelPath)> Requests { get; } = [];
        public bool Stopped { get; private set; }
        public string? Status => "準備完了 (テスト)";
        public event Action? TranslationReady;

        public void EnsureStarted(string modelPath) => Stopped = false;
        public void StopIfUnused() => Stopped = true;
        public bool IsTranslating(string japaneseText, TranslationTarget target) => Pending.Contains((target, japaneseText));

        public string? TranslateOrRequest(string japaneseText, TranslationTarget target, string modelPath, int debounceMs = 120)
        {
            Requests.Add((target, japaneseText, modelPath));
            if (Results.TryGetValue((target, japaneseText), out var translated)) return translated;
            Pending.Add((target, japaneseText));
            return null;
        }

        public void Complete(TranslationTarget target, string japaneseText, string translated)
        {
            Pending.Remove((target, japaneseText));
            Results[(target, japaneseText)] = translated;
            TranslationReady?.Invoke();
        }

        public void Dispose() { }
    }

    private sealed class MapConverter(Dictionary<string, ConversionClause[]> clauses) : TestConverter
    {
        private readonly FakeConverter _inner = new();
        public override string? Convert(string hiragana, string? before) =>
            ConvertClauses(hiragana, before) is { } parts ? string.Concat(parts.Select(c => c.Text)) : _inner.Convert(hiragana, before);
        public override IReadOnlyList<ConversionClause>? ConvertClauses(string hiragana, string? before) =>
            clauses.TryGetValue(hiragana, out var result) ? result : _inner.ConvertClauses(hiragana, before);
    }

    [Test]
    public static void TranslationMode_DefaultIsOff()
    {
        var settings = new Settings();
        Assert.Equal(TranslationTarget.Off, settings.TranslationMode, "翻訳モードは既定で OFF");
        Assert.Equal("", settings.TranslationModelPath, "モデルパスは空 (既定)");
    }

    [Test]
    public static void TranslationMode_PrependsTranslatedCandidateAndKeepsOriginal()
    {
        var translator = new FakeTranslator();
        translator.Results[(TranslationTarget.English, "こんにちは")] = "Hello";
        var converter = new MapConverter(new() { ["こんにちは"] = [new("こんにちは", "こんにちは")] });
        var history = new ConversionHistory(null);

        var k = new Keyboard(converter: converter, history: history, textTranslator: translator);
        k.Settings.TranslationMode = TranslationTarget.English;
        k.Type("konnnichiha ");

        var view = k.Host.View!;
        Assert.Equal("Hello", view.Text, "翻訳結果が第 1 候補");
        Assert.Equal("Hello", view.Candidates[0]);
        Assert.Equal("こんにちは", view.Candidates[1], "元の日本語が第 2 候補に残る");
        Assert.Equal(CandidateNote.Translation, view.Notes![0], "翻訳の注記が付く");
        Assert.Equal("原文: こんにちは", view.Meaning, "意味欄に元の日本語が出る");
        Assert.Equal("翻訳(英語)", view.ContextHint);

        // Space で元の日本語に戻せる
        k.Press(VirtualKeys.Space);
        Assert.Equal("こんにちは", k.Host.View!.Text, "Space で元の日本語に戻せる");
        Assert.True(k.Host.View.ContextHint is null, "元の日本語を選んでいるときは翻訳ヒントを消す");

        // 翻訳候補を確定しても学習に入れない
        k.Press(VirtualKeys.Up);
        Assert.Equal("Hello", k.Host.View!.Text);
        k.Press(VirtualKeys.Return);
        Assert.Equal("Hello", k.Host.Document);
        Assert.True(history.Get("こんにちは") is null, "翻訳候補は読みの学習に入れない");
    }

    [Test]
    public static void TranslationMode_SupportsMultipleTargetLanguages()
    {
        var translator = new FakeTranslator();
        translator.Results[(TranslationTarget.Korean, "ありがとう")] = "감사합니다";
        translator.Results[(TranslationTarget.Chinese, "ありがとう")] = "谢谢";
        var converter = new MapConverter(new() { ["ありがとう"] = [new("ありがとう", "ありがとう")] });

        var k = new Keyboard(converter: converter, textTranslator: translator);
        k.Settings.TranslationMode = TranslationTarget.Korean;
        k.Type("arigatou ");
        Assert.Equal("감사합니다", k.Host.View!.Text);
        Assert.Equal("翻訳(韓国語)", k.Host.View.ContextHint);
        k.Press(VirtualKeys.Escape);
        k.Press(VirtualKeys.Escape);

        k.Settings.TranslationMode = TranslationTarget.Chinese;
        k.Type("arigatou ");
        Assert.Equal("谢谢", k.Host.View!.Text);
        Assert.Equal("翻訳(中国語)", k.Host.View.ContextHint);
    }

    [Test]
    public static void TranslationMode_ReconversionTranslatesSelectedJapaneseDirectly()
    {
        var translator = new FakeTranslator();
        translator.Results[(TranslationTarget.French, "おはようございます")] = "Bonjour";

        var k = new Keyboard(textTranslator: translator);
        k.Settings.TranslationMode = TranslationTarget.French;
        k.Host.Selection = new ReconversionSelection("おはようございます", "おはようございます");
        k.Press(VirtualKeys.Convert);

        var view = k.Host.View!;
        Assert.Equal("Bonjour", view.Text);
        Assert.Equal("おはようございます", view.Candidates[1]);
        Assert.Equal("原文: おはようございます", view.Meaning);
        k.Press(VirtualKeys.Return);
        Assert.Equal("Bonjour", k.Host.Document);
    }

    [Test]
    public static void TranslationMode_SkipsUrlsAndEnglishOnlyText()
    {
        var translator = new FakeTranslator();
        var converter = new MapConverter(new()
        {
            ["えいご"] = [new("えいご", "英語")],
        });

        var k = new Keyboard(converter: converter, textTranslator: translator);
        k.Settings.TranslationMode = TranslationTarget.English;
        k.Type("hello ");
        Assert.True(translator.Requests.Count == 0, "英字のみの入力は翻訳しない");
    }

    [Test]
    public static void TranslationMode_ShowsTranslatingHintWhileWaiting()
    {
        var translator = new FakeTranslator();
        var converter = new MapConverter(new() { ["こんにちは"] = [new("こんにちは", "こんにちは")] });

        var k = new Keyboard(converter: converter, textTranslator: translator);
        k.Settings.TranslationMode = TranslationTarget.English;
        k.Type("konnnichiha ");

        Assert.Equal("こんにちは", k.Host.View!.Text, "翻訳完了までは元の日本語を出す");
        Assert.Equal("翻訳中(英語)…", k.Host.View.ContextHint, "翻訳待ち中は翻訳中ヒントを出す");

        translator.Complete(TranslationTarget.English, "こんにちは", "Hello");
        k.NotifyConversionFinished();
        Assert.Equal("Hello", k.Host.View!.Text, "翻訳完了後のリフレッシュで訳文が先頭になる");
        Assert.Equal("翻訳(英語)", k.Host.View.ContextHint);
    }

    [Test]
    public static void TranslationMode_ReconversionRefreshesWhenUncachedCompletes()
    {
        var translator = new FakeTranslator();
        var k = new Keyboard(textTranslator: translator);
        k.Settings.TranslationMode = TranslationTarget.Korean;
        k.Host.Selection = new ReconversionSelection("よろしくお願いします", "よろしくおねがいします");
        k.Press(VirtualKeys.Convert);

        Assert.Equal("よろしくお願いします", k.Host.View!.Text, "未キャッシュの再変換は元の文のまま待つ");
        Assert.Equal("翻訳中(韓国語)…", k.Host.View.ContextHint);

        translator.Complete(TranslationTarget.Korean, "よろしくお願いします", "잘 부탁드립니다");
        k.NotifyConversionFinished();
        Assert.Equal("잘 부탁드립니다", k.Host.View!.Text, "完了時に自動で訳文へ差し替わる");
        Assert.Equal("よろしくお願いします", k.Host.View.Candidates[1]);
        Assert.Equal("翻訳(韓国語)", k.Host.View.ContextHint);
    }

    [Test]
    public static void LlamaServerTranslator_SelectsChatTemplateByModelAndCleansOutput()
    {
        var (qwenPrompt, qwenStops) = LlamaServerTranslator.BuildPromptAndStops("Qwen2.5-1.5B-Instruct-Q4_K_M.gguf", TranslationTarget.English, "こんにちは");
        Assert.True(qwenPrompt.Contains("<|im_start|>user", StringComparison.Ordinal), qwenPrompt);
        Assert.True(qwenStops.Contains("<|im_end|>"), string.Join(",", qwenStops));

        var (gemmaPrompt, gemmaStops) = LlamaServerTranslator.BuildPromptAndStops("gemma-3-1b-it.gguf", TranslationTarget.Korean, "こんにちは");
        Assert.True(gemmaPrompt.Contains("<start_of_turn>user", StringComparison.Ordinal), gemmaPrompt);
        Assert.True(gemmaStops.Contains("<end_of_turn>"), string.Join(",", gemmaStops));

        var (llamaPrompt, llamaStops) = LlamaServerTranslator.BuildPromptAndStops("Llama-3.2-1B.gguf", TranslationTarget.French, "こんにちは");
        Assert.True(llamaPrompt.Contains("<|start_header_id|>user<|end_header_id|>", StringComparison.Ordinal), llamaPrompt);
        Assert.True(llamaStops.Contains("<|eot_id|>"), string.Join(",", llamaStops));

        Assert.Equal("Hello", LlamaServerTranslator.CleanOutput("<think>考え中</think> \"Hello\"", TranslationTarget.English), "思考タグと引用符を除去する");
        Assert.Equal("감사합니다", LlamaServerTranslator.CleanOutput("Korean: 감사합니다", TranslationTarget.Korean));
        Assert.Equal("", LlamaServerTranslator.CleanOutput("こんにちは", TranslationTarget.English), "英字が無い出力は失敗扱い");
        Assert.Equal("", LlamaServerTranslator.CleanOutput("ありがとうございます", TranslationTarget.Korean), "ハングルが無い出力は失敗扱い");
        Assert.Equal("", LlamaServerTranslator.CleanOutput("これはテストです", TranslationTarget.Chinese), "かなが残る中国語出力は失敗扱い");
        Assert.Equal("谢谢", LlamaServerTranslator.CleanOutput("谢谢", TranslationTarget.Chinese));
    }

    [Test]
    public static void TranslationMode_SpotTranslationWorksEvenWhenModeIsOff()
    {
        var translator = new FakeTranslator();
        translator.Results[(TranslationTarget.Korean, "ありがとう")] = "감사합니다";
        var converter = new MapConverter(new()
        {
            ["ありがとうをかんこくご"] = [new("ありがとう", "ありがとう"), new("を", "を"), new("かんこくご", "韓国語")],
        });

        var k = new Keyboard(converter: converter, textTranslator: translator);
        Assert.Equal(TranslationTarget.Off, k.Settings.TranslationMode);
        k.Type("arigatouwokankokugo ");

        Assert.Equal("감사합니다", k.Host.View!.Text, "翻訳モード OFF でも 〇〇を〇〇語 でスポット翻訳できる");
        Assert.Equal("ありがとうを韓国語", k.Host.View.Candidates[1]);
        Assert.Equal("原文: ありがとう", k.Host.View.Meaning);
        Assert.Equal("翻訳(韓国語)", k.Host.View.ContextHint);
        k.Press(VirtualKeys.Space);
        Assert.Equal("ありがとうを韓国語", k.Host.View.Text, "Space で元の表現に戻せる");
    }

    [Test]
    public static void TranslationMode_LiveTranslationTranslatesWhenEnabled()
    {
        var translator = new FakeTranslator();
        var converter = new MapConverter(new() { ["こんにちは"] = [new("こんにちは", "こんにちは")] });

        var k = new Keyboard(live: true, converter: converter, textTranslator: translator);
        k.Settings.TranslationMode = TranslationTarget.English;
        k.Settings.LiveTranslation = false;
        k.Type("konnnichiha");
        Assert.Equal("こんにちは", k.Host.View!.Text, "LiveTranslation OFF ではライブ中に翻訳しない");

        k.Press(VirtualKeys.Escape);
        k.Settings.LiveTranslation = true;
        k.Type("konnnichiha");
        Assert.Equal("こんにちは", k.Host.View!.Text, "未キャッシュ時は日本語を出しつつ裏で翻訳を予約する");
        translator.Complete(TranslationTarget.English, "こんにちは", "Hello");
        k.NotifyConversionFinished();
        Assert.Equal("Hello", k.Host.View!.Text, "ライブ翻訳完了後にリアルタイムで訳文へ切り替わる");
        k.Press(VirtualKeys.Return);
        Assert.Equal("Hello", k.Host.Document);
    }
}
