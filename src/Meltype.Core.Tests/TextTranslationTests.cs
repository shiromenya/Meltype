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

internal static class TextTranslationTests
{
    private sealed class FakeTranslator : ITextTranslator
    {
        public Dictionary<(TranslationTarget Target, string Text), string> Results { get; } = new();
        public List<(TranslationTarget Target, string Text, string ModelPath)> Requests { get; } = [];
        public bool Stopped { get; private set; }
        public string? Status => "準備完了 (テスト)";
        public event Action? TranslationReady;

        public void EnsureStarted(string modelPath) => Stopped = false;
        public void StopIfUnused() => Stopped = true;
        public bool IsTranslating(string japaneseText, TranslationTarget target) => false;

        public string? TranslateOrRequest(string japaneseText, TranslationTarget target, string modelPath)
        {
            Requests.Add((target, japaneseText, modelPath));
            return Results.TryGetValue((target, japaneseText), out var translated) ? translated : null;
        }

        public void Complete(TranslationTarget target, string japaneseText, string translated)
        {
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
}
