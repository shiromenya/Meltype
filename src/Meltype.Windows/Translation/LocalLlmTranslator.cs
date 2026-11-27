// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 shiromenya

using System.Collections.Concurrent;
using System.Text;
using LLama;
using LLama.Common;
using LLama.Sampling;
using Meltype.Config;
using Meltype.Diagnostics;
using Meltype.Engine;

namespace Meltype.Translation;

/// <summary>
/// LLamaSharp (llama.cpp CPU バックエンド) と軽量 GGUF モデルを用いたローカル多言語翻訳サービス。
/// 翻訳モードが OFF の間はモデルを解放し、ON にしたときだけ遅延起動する。
/// </summary>
internal sealed class LocalLlmTranslator(string dataDirectory) : ITextTranslator
{
    private const string DefaultModelFileName = "CAT-Translate-0.8b.Q4_K_M.gguf";
    private const string DefaultModelDownloadUrl =
        "https://huggingface.co/mradermacher/CAT-Translate-0.8b-GGUF/resolve/main/CAT-Translate-0.8b.Q4_K_M.gguf";
    private const int DebounceMs = 120;
    private const int MaxCacheEntries = 2048;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(15) };

    private readonly string _defaultModelPath = Path.Combine(dataDirectory, "models", DefaultModelFileName);
    private readonly ConcurrentDictionary<(TranslationTarget Target, string Text), string> _cache = new();
    private readonly SemaphoreSlim _inferGate = new(1, 1);
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly object _stateGate = new();

    private LLamaWeights? _weights;
    private StatelessExecutor? _executor;
    private string? _loadedModelPath;
    private Task? _initTask;
    private CancellationTokenSource? _pendingCts;
    private (TranslationTarget Target, string Text)? _translatingKey;
    private volatile bool _disposed;

    public event Action? TranslationReady;

    public string? Status { get; private set; } = "未起動 (ON でモデル準備)";

    public bool IsTranslating(string japaneseText, TranslationTarget target) =>
        _translatingKey == (target, NormalizeKey(japaneseText));

    public void EnsureStarted(string modelPath)
    {
        if (_disposed) return;
        var resolved = ResolveModelPath(modelPath);
        lock (_stateGate)
        {
            if (_disposed) return;
            if (_executor is not null && string.Equals(_loadedModelPath, resolved, StringComparison.OrdinalIgnoreCase))
                return;
            if (_initTask is { IsCompleted: false } && string.Equals(_loadedModelPath, resolved, StringComparison.OrdinalIgnoreCase))
                return;
            ReleaseModelLocked();
            _loadedModelPath = resolved;
            _initTask = Task.Run(() => InitializeAsync(resolved));
        }
    }

    public void StopIfUnused()
    {
        lock (_stateGate)
        {
            _pendingCts?.Cancel();
            _translatingKey = null;
            if (_executor is null && _weights is null) return;
            ReleaseModelLocked();
            Status = "停止中 (翻訳モード OFF)";
        }
    }

    private string ResolveModelPath(string modelPath) =>
        string.IsNullOrWhiteSpace(modelPath) ? _defaultModelPath : modelPath.Trim();

    private void ReleaseModelLocked()
    {
        _executor = null;
        var weights = _weights;
        _weights = null;
        _loadedModelPath = null;
        if (weights is null) return;
        _ = Task.Run(() =>
        {
            if (_inferGate.Wait(TimeSpan.FromSeconds(2)))
            {
                try { weights.Dispose(); }
                finally { _inferGate.Release(); }
            }
        });
    }

    public string? TranslateOrRequest(string japaneseText, TranslationTarget target, string modelPath)
    {
        if (_disposed || target == TranslationTarget.Off || string.IsNullOrWhiteSpace(japaneseText))
            return null;
        var normalized = NormalizeKey(japaneseText);
        if (normalized.Length < 2 || !ContainsJapanese(normalized))
            return null;
        var key = (target, normalized);
        if (_cache.TryGetValue(key, out var cached))
            return cached;

        EnsureStarted(modelPath);
        if (_executor is null) return null;

        ScheduleTranslation(target, normalized);
        return null;
    }

    private static string NormalizeKey(string text) =>
        text.Trim().TrimEnd('。', '．', '、', '，');

    private void ScheduleTranslation(TranslationTarget target, string japaneseText)
    {
        CancellationTokenSource cts;
        CancellationToken token;
        var key = (target, japaneseText);
        lock (_stateGate)
        {
            if (_disposed) return;
            if (_translatingKey == key && _pendingCts is { IsCancellationRequested: false })
                return;
            _pendingCts?.Cancel();
            cts = new CancellationTokenSource();
            token = cts.Token;
            _pendingCts = cts;
            _translatingKey = key;
        }

        _ = Task.Run(() => RunDebouncedTranslationAsync(target, japaneseText, cts, token));
    }

    private async Task RunDebouncedTranslationAsync(TranslationTarget target, string japaneseText, CancellationTokenSource cts, CancellationToken ct)
    {
        var key = (target, japaneseText);
        try
        {
            await Task.Delay(DebounceMs, ct).ConfigureAwait(false);
            if (_disposed || _executor is not { } executor || ct.IsCancellationRequested) return;

            await _inferGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_disposed || ct.IsCancellationRequested || _cache.ContainsKey(key)) return;

                var prompt = BuildPrompt(target, japaneseText);
                var maxTokens = Math.Clamp(japaneseText.Length * 3 + 16, 24, 96);
                var inferenceParams = new InferenceParams
                {
                    MaxTokens = maxTokens,
                    AntiPrompts = ["</s>", "<|user|>", "<|system|>", "<|im_end|>", "\n", "\r"],
                    SamplingPipeline = new DefaultSamplingPipeline { Temperature = 0f },
                };

                var sb = new StringBuilder();
                await foreach (var token in executor.InferAsync(prompt, inferenceParams, ct).ConfigureAwait(false))
                {
                    sb.Append(token);
                }

                if (_disposed || ct.IsCancellationRequested) return;

                var result = CleanOutput(sb.ToString(), target);
                if (!string.IsNullOrEmpty(result))
                {
                    if (_cache.Count >= MaxCacheEntries) _cache.Clear();
                    _cache[key] = result;
                    TranslationReady?.Invoke();
                }
            }
            finally
            {
                try { _inferGate.Release(); } catch (ObjectDisposedException) { }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            Log.Warn($"ローカルLLM翻訳に失敗: {ex.Message}");
        }
        finally
        {
            lock (_stateGate)
            {
                if (ReferenceEquals(_pendingCts, cts))
                    _pendingCts = null;
                if (_translatingKey == key)
                    _translatingKey = null;
            }
            cts.Dispose();
        }
    }

    private async Task InitializeAsync(string modelPath)
    {
        try
        {
            if (!File.Exists(modelPath))
            {
                if (!string.Equals(modelPath, _defaultModelPath, StringComparison.OrdinalIgnoreCase))
                {
                    Status = $"モデルが見つかりません: {Path.GetFileName(modelPath)}";
                    return;
                }
                await DownloadDefaultModelAsync(modelPath).ConfigureAwait(false);
            }

            if (_disposed) return;
            Status = "モデルを読み込み中...";

            var threads = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
            var modelParams = new ModelParams(modelPath)
            {
                ContextSize = 256,
                BatchSize = 128,
                Threads = threads,
                BatchThreads = threads,
                GpuLayerCount = 0,
            };

            var weights = await LLamaWeights.LoadFromFileAsync(modelParams, _disposeCts.Token).ConfigureAwait(false);
            if (_disposed || !string.Equals(_loadedModelPath, modelPath, StringComparison.OrdinalIgnoreCase))
            {
                weights.Dispose();
                return;
            }

            _weights = weights;
            _executor = new StatelessExecutor(weights, modelParams);
            Status = $"準備完了 ({Path.GetFileName(modelPath)})";
            Log.Info($"ローカルLLM翻訳エンジンの準備が完了しました: {modelPath}");
            TranslationReady?.Invoke();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Status = $"初期化エラー: {ex.Message}";
            Log.Warn($"ローカルLLM翻訳エンジンの初期化に失敗: {ex}");
        }
    }

    private async Task DownloadDefaultModelAsync(string modelPath)
    {
        var dir = Path.GetDirectoryName(modelPath)!;
        Directory.CreateDirectory(dir);
        Status = "モデルをダウンロード中 (0%)...";
        Log.Info($"ローカルLLMモデルのダウンロードを開始します: {DefaultModelDownloadUrl}");
        var partPath = modelPath + ".part";
        using var response = await Http.GetAsync(DefaultModelDownloadUrl, HttpCompletionOption.ResponseHeadersRead, _disposeCts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var totalBytes = response.Content.Headers.ContentLength ?? -1L;

        await using (var src = await response.Content.ReadAsStreamAsync(_disposeCts.Token).ConfigureAwait(false))
        await using (var dst = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            var buffer = new byte[81920];
            long downloaded = 0;
            var lastPercent = -1;
            while (!_disposed)
            {
                var read = await src.ReadAsync(buffer, _disposeCts.Token).ConfigureAwait(false);
                if (read == 0) break;
                await dst.WriteAsync(buffer.AsMemory(0, read), _disposeCts.Token).ConfigureAwait(false);
                downloaded += read;
                if (totalBytes > 0)
                {
                    var pct = (int)(downloaded * 100 / totalBytes);
                    if (pct != lastPercent)
                    {
                        lastPercent = pct;
                        Status = $"モデルをダウンロード中 ({pct}%)";
                    }
                }
            }
        }

        if (_disposed) return;
        if (File.Exists(modelPath)) File.Delete(modelPath);
        File.Move(partPath, modelPath);
        Log.Info($"ローカルLLMモデルのダウンロードが完了しました: {modelPath}");
    }

    private static string BuildPrompt(TranslationTarget target, string japaneseText) =>
        $"<|user|>Translate the following Japanese text into {target.PromptLanguageName()}. Output only the translated text.\n\n{japaneseText}</s><|assistant|>";

    internal static string CleanOutput(string raw, TranslationTarget target)
    {
        var text = raw
            .Replace("</s>", "", StringComparison.Ordinal)
            .Replace("<s>", "", StringComparison.Ordinal)
            .Replace("<|assistant|>", "", StringComparison.Ordinal)
            .Replace("<|im_end|>", "", StringComparison.Ordinal)
            .Replace("<|endoftext|>", "", StringComparison.Ordinal)
            .Trim();
        var newlineIdx = text.IndexOfAny(['\r', '\n']);
        if (newlineIdx >= 0) text = text[..newlineIdx].Trim();
        foreach (var marker in (ReadOnlySpan<string>)["translates to ", "means ", "translation is "])
        {
            var markerIdx = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIdx >= 0)
            {
                text = text[(markerIdx + marker.Length)..].Trim();
                break;
            }
        }
        foreach (var prefix in (ReadOnlySpan<string>)["Translation:", "English:", "Chinese:", "Korean:", "French:", "Spanish:", "German:", "Output:"])
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                text = text[prefix.Length..].Trim();
                break;
            }
        }
        if (text.Length >= 2 && ((text.StartsWith('"') && text.EndsWith('"')) || (text.StartsWith('\'') && text.EndsWith('\'')) || (text.StartsWith('「') && text.EndsWith('」'))))
            text = text[1..^1].Trim();

        return IsValidOutputForTarget(text, target) ? text : "";
    }

    internal static bool IsValidOutputForTarget(string text, TranslationTarget target)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return target switch
        {
            TranslationTarget.English or TranslationTarget.French or TranslationTarget.Spanish or TranslationTarget.German =>
                text.Any(c => char.IsLetter(c) && c < 0x0250),
            TranslationTarget.Korean => text.Any(c => c is >= '\uAC00' and <= '\uD7AF' or >= '\u1100' and <= '\u11FF'),
            TranslationTarget.Chinese => !text.Any(c => c is (>= '\u3040' and <= '\u30FF')),
            _ => false,
        };
    }

    private static bool ContainsJapanese(string text)
    {
        foreach (var c in text)
        {
            if (c is (>= '\u3040' and <= '\u30FF') or (>= '\u4E00' and <= '\u9FFF'))
                return true;
        }
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _disposeCts.Cancel();
        lock (_stateGate)
        {
            _pendingCts?.Cancel();
        }
        var acquired = false;
        try
        {
            acquired = _inferGate.Wait(TimeSpan.FromSeconds(2));
            _weights?.Dispose();
            _weights = null;
            _executor = null;
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            if (acquired)
            {
                try { _inferGate.Release(); } catch (ObjectDisposedException) { }
            }
        }
        _inferGate.Dispose();
        _disposeCts.Dispose();
    }
}
