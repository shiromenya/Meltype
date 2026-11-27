// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 shiromenya

using Meltype.Config;

namespace Meltype.Engine;

/// <summary>
/// ローカル LLM などを用いて、変換時の日本語文を指定言語へ翻訳するサービス。
/// <see cref="TranslateOrRequest"/> は非ブロッキングで即座にキャッシュを返し、未翻訳ならバックグラウンドで翻訳して
/// 完了時に <see cref="TranslationReady"/> を発火する。
/// </summary>
public interface ITextTranslator : IDisposable
{
    /// <summary>翻訳が完了してキャッシュに入ったときに発火する (ワーカースレッドから呼ばれることがある)。</summary>
    event Action? TranslationReady;

    /// <summary>現在の状態 (ダウンロード中・読み込み中・準備完了・停止中など)。トレイメニュー表示用。</summary>
    string? Status { get; }

    /// <summary>指定モデルでモデルの準備・読み込みをバックグラウンドで開始する (modelPath が空なら既定モデル)。</summary>
    void EnsureStarted(string modelPath);

    /// <summary>翻訳モードが OFF のときにモデルとメモリを解放して待機状態に戻す。</summary>
    void StopIfUnused();

    /// <summary>
    /// 指定した日本語文の訳文がキャッシュにあれば返す。
    /// キャッシュにない場合はバックグラウンド翻訳を予約して null を返す。
    /// </summary>
    string? TranslateOrRequest(string japaneseText, TranslationTarget target, string modelPath, int debounceMs = 120);

    /// <summary>指定した日本語文がいま翻訳待ち・翻訳中であるか。</summary>
    bool IsTranslating(string japaneseText, TranslationTarget target);
}

public static class TranslationTargetExtensions
{
    public static string ShortLabel(this TranslationTarget target) => target switch
    {
        TranslationTarget.Off => "OFF",
        TranslationTarget.English => "英語",
        TranslationTarget.Chinese => "中国語",
        TranslationTarget.Korean => "韓国語",
        TranslationTarget.French => "フランス語",
        TranslationTarget.Spanish => "スペイン語",
        TranslationTarget.German => "ドイツ語",
        _ => target.ToString(),
    };

    public static string PromptLanguageName(this TranslationTarget target) => target switch
    {
        TranslationTarget.English => "English",
        TranslationTarget.Chinese => "Simplified Chinese",
        TranslationTarget.Korean => "Korean",
        TranslationTarget.French => "French",
        TranslationTarget.Spanish => "Spanish",
        TranslationTarget.German => "German",
        _ => "English",
    };
}
