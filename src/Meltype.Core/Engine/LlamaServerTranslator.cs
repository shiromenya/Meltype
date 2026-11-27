// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 shiromenya

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Meltype.Config;
using Meltype.Diagnostics;

namespace Meltype.Engine;

/// <summary>
/// Zenzai と同じ同梱の llama-server (HTTP API) を使って、全 OS 共通でローカル多言語翻訳を行うサービス。
/// 翻訳モードが OFF の間は llama-server を起動せず (OFF に戻したら停止してメモリ解放)、ON のときだけ遅延起動する。
/// モデル切り替え時はキャッシュをクリアし、GGUF ごとのチャットテンプレートに対応する。
/// </summary>
internal sealed partial class LlamaServerTranslator : ITextTranslator
{
    private const string DefaultModelFileName = "CAT-Translate-0.8b.Q4_K_M.gguf";
    private const string DefaultModelDownloadUrl =
        "https://huggingface.co/mradermacher/CAT-Translate-0.8b-GGUF/resolve/main/CAT-Translate-0.8b.Q4_K_M.gguf";
    private const int StartTimeoutMs = 30_000;
    private const int InferTimeoutMs = 10_000;
    private const int DebounceMs = 120;
    private const int MaxCacheEntries = 2048;
    private const long SpotIdleTimeoutMs = 5 * 60 * 1000;

    private static readonly HttpClient DownloadHttp = new() { Timeout = TimeSpan.FromMinutes(15) };

    private readonly string _defaultModelPath;
    private readonly Func<string> _serverPathProvider;
    private readonly Func<string> _deviceProvider;
    private readonly Func<bool> _isPersistentProvider;
    private readonly Uri? _fixedEndpoint;
    private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly ConcurrentDictionary<(TranslationTarget Target, string Text), string> _cache = new();
    private readonly SemaphoreSlim _inferGate = new(1, 1);
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly object _stateGate = new();
    private readonly Timer? _idleTimer;

    private Process? _process;
    private string? _pidFile;
    private Uri? _endpoint;
    private string? _loadedModelPath;
    private Task? _initTask;
    private CancellationTokenSource? _pendingCts;
    private (TranslationTarget Target, string Text)? _translatingKey;
    private long _lastUsedTick = Environment.TickCount64;
    private volatile bool _disposed;

    public LlamaServerTranslator(string dataDirectory, Func<string> serverPathProvider, Func<string> deviceProvider, Func<bool>? isPersistentProvider = null)
    {
        _defaultModelPath = Path.Combine(dataDirectory, "models", DefaultModelFileName);
        _serverPathProvider = serverPathProvider;
        _deviceProvider = deviceProvider;
        _isPersistentProvider = isPersistentProvider ?? (() => false);
        _idleTimer = new Timer(_ => CheckIdleStop(), null, 60_000, 60_000);
    }

    /// <summary>すでに動いている llama-server (またはテスト用偽サーバー) を使うコンストラクタ。</summary>
    internal LlamaServerTranslator(Uri fixedEndpoint, string modelName = DefaultModelFileName)
    {
        _defaultModelPath = modelName;
        _serverPathProvider = () => "";
        _deviceProvider = () => "";
        _isPersistentProvider = () => false;
        _fixedEndpoint = fixedEndpoint;
        _endpoint = fixedEndpoint;
        _loadedModelPath = modelName;
        Status = $"準備完了 ({modelName})";
    }

    public event Action? TranslationReady;

    public string? Status { get; private set; } = "未起動 (ON でモデル準備)";

    public bool IsTranslating(string japaneseText, TranslationTarget target) =>
        _translatingKey == (target, NormalizeKey(japaneseText));

    public void EnsureStarted(string modelPath)
    {
        if (_disposed || _fixedEndpoint is not null) return;
        var resolved = ResolveModelPath(modelPath);
        lock (_stateGate)
        {
            if (_disposed) return;
            if (_endpoint is not null && string.Equals(_loadedModelPath, resolved, StringComparison.OrdinalIgnoreCase))
                return;
            if (_initTask is { IsCompleted: false } && string.Equals(_loadedModelPath, resolved, StringComparison.OrdinalIgnoreCase))
                return;
            StopServerLocked(clearCache: true);
            _loadedModelPath = resolved;
            _initTask = Task.Run(() => InitializeAsync(resolved));
        }
    }

    public void StopIfUnused()
    {
        if (_fixedEndpoint is not null)
        {
            _cache.Clear();
            return;
        }
        lock (_stateGate)
        {
            _pendingCts?.Cancel();
            _translatingKey = null;
            if (_process is null && _endpoint is null && _loadedModelPath is null) return;
            StopServerLocked(clearCache: true);
            Status = "停止中 (翻訳モード OFF)";
        }
    }

    private string ResolveModelPath(string modelPath) =>
        string.IsNullOrWhiteSpace(modelPath) ? _defaultModelPath : modelPath.Trim();

    private void StopServerLocked(bool clearCache)
    {
        _endpoint = null;
        _loadedModelPath = null;
        if (clearCache) _cache.Clear();
        var process = _process;
        _process = null;
        var pidFile = _pidFile;
        _pidFile = null;
        if (process is not null)
        {
            ZenzaiConverter.Kill(process);
            process.Dispose();
        }
        DeletePidFile(pidFile);
    }

    private void CheckIdleStop()
    {
        if (_disposed || _isPersistentProvider()) return;
        if (Environment.TickCount64 - Interlocked.Read(ref _lastUsedTick) >= SpotIdleTimeoutMs)
            StopIfUnused();
    }

    public string? TranslateOrRequest(string japaneseText, TranslationTarget target, string modelPath, int debounceMs = DebounceMs)
    {
        if (_disposed || target == TranslationTarget.Off || string.IsNullOrWhiteSpace(japaneseText))
            return null;
        var normalized = NormalizeKey(japaneseText);
        if (normalized.Length < 2 || !ContainsJapanese(normalized))
            return null;
        Interlocked.Exchange(ref _lastUsedTick, Environment.TickCount64);

        // モデルパスが切り替わった場合はキャッシュを破棄して再起動する
        if (_fixedEndpoint is null)
        {
            var resolved = ResolveModelPath(modelPath);
            if (!string.Equals(_loadedModelPath, resolved, StringComparison.OrdinalIgnoreCase))
                EnsureStarted(modelPath);
        }

        var key = (target, normalized);
        if (_cache.TryGetValue(key, out var cached))
            return cached;

        if (_endpoint is null) return null;

        ScheduleTranslation(target, normalized, debounceMs);
        return null;
    }

    private static string NormalizeKey(string text) =>
        text.Trim().TrimEnd('。', '．', '、', '，');

    private void ScheduleTranslation(TranslationTarget target, string japaneseText, int debounceMs)
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

        _ = Task.Run(() => RunDebouncedTranslationAsync(target, japaneseText, debounceMs, cts, token));
    }

    private async Task RunDebouncedTranslationAsync(TranslationTarget target, string japaneseText, int debounceMs, CancellationTokenSource cts, CancellationToken ct)
    {
        var key = (target, japaneseText);
        try
        {
            await Task.Delay(Math.Max(0, debounceMs), ct).ConfigureAwait(false);
            var endpoint = _endpoint;
            var modelName = Path.GetFileName(_loadedModelPath ?? _defaultModelPath);
            if (_disposed || endpoint is null || ct.IsCancellationRequested) return;

            await _inferGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_disposed || ct.IsCancellationRequested || _cache.ContainsKey(key)) return;
                endpoint = _endpoint;
                if (endpoint is null) return;

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
                timeoutCts.CancelAfter(InferTimeoutMs);

                // /v1/chat/completions は GGUF メタデータの公式チャットテンプレートを llama-server 側で自動適用する。
                // 未対応またはエラーのときだけ /completion + モデル名別テンプレートへフォールバックする。
                var raw = await RequestChatCompletionAsync(endpoint, target, japaneseText, timeoutCts.Token).ConfigureAwait(false)
                    ?? await RequestCompletionAsync(endpoint, modelName, target, japaneseText, timeoutCts.Token).ConfigureAwait(false);

                if (_disposed || ct.IsCancellationRequested || raw is null) return;

                var result = CleanOutput(raw, target);
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

    private async Task<string?> RequestChatCompletionAsync(Uri endpoint, TranslationTarget target, string japaneseText, CancellationToken ct)
    {
        try
        {
            var maxTokens = Math.Clamp(japaneseText.Length * 3 + 16, 24, 96);
            var body = WriteJson(w =>
            {
                w.WriteStartObject();
                w.WriteStartArray("messages");
                w.WriteStartObject();
                w.WriteString("role", "user");
                w.WriteString("content", $"Translate the following Japanese text into {target.PromptLanguageName()}. Output only the translated text.\n\n{japaneseText}");
                w.WriteEndObject();
                w.WriteEndArray();
                w.WriteNumber("temperature", 0);
                w.WriteNumber("max_tokens", maxTokens);
                w.WriteStartArray("stop");
                w.WriteStringValue("\n");
                w.WriteStringValue("\r");
                w.WriteEndArray();
                w.WriteEndObject();
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, "v1/chat/completions"))
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var jsonText = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(jsonText);
            if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0 &&
                choices[0].TryGetProperty("message", out var msg) &&
                msg.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.String)
            {
                return content.GetString();
            }
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException)
        {
            return null;
        }
    }

    private async Task<string?> RequestCompletionAsync(Uri endpoint, string modelName, TranslationTarget target, string japaneseText, CancellationToken ct)
    {
        try
        {
            var (prompt, stops) = BuildPromptAndStops(modelName, target, japaneseText);
            var maxTokens = Math.Clamp(japaneseText.Length * 3 + 16, 24, 96);
            var body = WriteJson(w =>
            {
                w.WriteStartObject();
                w.WriteString("prompt", prompt);
                w.WriteNumber("n_predict", maxTokens);
                w.WriteNumber("temperature", 0);
                w.WriteNumber("top_k", 1);
                w.WriteBoolean("cache_prompt", true);
                w.WriteStartArray("stop");
                foreach (var s in stops) w.WriteStringValue(s);
                w.WriteEndArray();
                w.WriteEndObject();
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, "completion"))
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var jsonText = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(jsonText);
            return doc.RootElement.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String
                ? content.GetString()
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException)
        {
            return null;
        }
    }

    private static string WriteJson(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer)) write(writer);
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private async Task InitializeAsync(string modelPath)
    {
        try
        {
            var serverPath = _serverPathProvider();
            if (!File.Exists(serverPath))
            {
                Status = "llama-server が見つかりません";
                return;
            }

            if (!File.Exists(modelPath))
            {
                if (!string.Equals(modelPath, _defaultModelPath, StringComparison.OrdinalIgnoreCase))
                {
                    Status = $"モデルが見つかりません: {Path.GetFileName(modelPath)}";
                    return;
                }
                await DownloadDefaultModelAsync(modelPath).ConfigureAwait(false);
            }

            if (_disposed || !string.Equals(_loadedModelPath, modelPath, StringComparison.OrdinalIgnoreCase)) return;
            Status = "llama-server を起動中...";

            var port = ZenzaiConverter.FreePort();
            var info = new ProcessStartInfo(serverPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var arg in ServerArguments(modelPath, port, _deviceProvider()))
                info.ArgumentList.Add(arg);
            if (!info.Environment.ContainsKey("LLAMA_ARG_THREADS"))
            {
                info.ArgumentList.Add("-t");
                info.ArgumentList.Add(Math.Min(Environment.ProcessorCount, 4).ToString(CultureInfo.InvariantCulture));
            }
            info.ArgumentList.Add("--poll");
            info.ArgumentList.Add("0");
            info.Environment.TryAdd("KMP_BLOCKTIME", "5");

            var process = Process.Start(info);
            if (process is null)
            {
                Status = "llama-server を起動できませんでした";
                return;
            }

            string? pidFile;
            using (var self = Process.GetCurrentProcess())
            {
                pidFile = ZenzaiConverter.RecordServer(process.Id, Path.GetFileNameWithoutExtension(serverPath), process.StartTime.ToUniversalTime().Ticks,
                    self.Id, self.StartTime.ToUniversalTime().Ticks);
            }
            process.OutputDataReceived += static (_, _) => { };
            process.ErrorDataReceived += static (_, _) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            lock (_stateGate)
            {
                if (_disposed || !string.Equals(_loadedModelPath, modelPath, StringComparison.OrdinalIgnoreCase))
                {
                    ZenzaiConverter.Kill(process);
                    process.Dispose();
                    DeletePidFile(pidFile);
                    return;
                }
                _process = process;
                _pidFile = pidFile;
            }

            var endpoint = new Uri($"http://127.0.0.1:{port}/");
            var deadline = Environment.TickCount64 + StartTimeoutMs;
            while (Environment.TickCount64 < deadline && !process.HasExited && !_disposed)
            {
                if (await PingAsync(endpoint, _disposeCts.Token).ConfigureAwait(false))
                {
                    lock (_stateGate)
                    {
                        if (!string.Equals(_loadedModelPath, modelPath, StringComparison.OrdinalIgnoreCase)) return;
                        _endpoint = endpoint;
                    }
                    Status = $"準備完了 ({Path.GetFileName(modelPath)})";
                    Log.Info($"ローカルLLM翻訳サーバーの準備が完了しました: {modelPath}");
                    TranslationReady?.Invoke();
                    return;
                }
                await Task.Delay(200, _disposeCts.Token).ConfigureAwait(false);
            }
            Status = "llama-server の起動がタイムアウトしました";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException or SocketException or ObjectDisposedException or HttpRequestException)
        {
            Status = $"初期化エラー: {ex.Message}";
            Log.Warn($"ローカルLLM翻訳サーバーの初期化に失敗: {ex}");
        }
    }

    internal static List<string> ServerArguments(string modelPath, int port, string device)
    {
        var arguments = new List<string>
        {
            "-m", modelPath,
            "--host", "127.0.0.1",
            "--port", port.ToString(CultureInfo.InvariantCulture),
            "-c", "512",
            "--parallel", "1",
        };
        if (device.Trim() is { Length: > 0 } name) arguments.AddRange(["--device", name]);
        else arguments.AddRange(["-ngl", "0"]);
        return arguments;
    }

    private async Task<bool> PingAsync(Uri endpoint, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(1000);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint, "health"));
            using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or ObjectDisposedException)
        {
            return false;
        }
    }

    private async Task DownloadDefaultModelAsync(string modelPath)
    {
        var dir = Path.GetDirectoryName(modelPath)!;
        Directory.CreateDirectory(dir);
        Status = "モデルをダウンロード中 (0%)...";
        Log.Info($"ローカルLLMモデルのダウンロードを開始します: {DefaultModelDownloadUrl}");
        var partPath = modelPath + ".part";
        using var response = await DownloadHttp.GetAsync(DefaultModelDownloadUrl, HttpCompletionOption.ResponseHeadersRead, _disposeCts.Token).ConfigureAwait(false);
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

    /// <summary>
    /// /completion フォールバック用に、モデル名から主要チャットテンプレート (Qwen / Gemma / Llama-3 / 既定) を自動選択する。
    /// </summary>
    internal static (string Prompt, string[] Stops) BuildPromptAndStops(string modelName, TranslationTarget target, string japaneseText)
    {
        var instruction = $"Translate the following Japanese text into {target.PromptLanguageName()}. Output only the translated text.\n\n{japaneseText}";
        var lower = modelName.ToLowerInvariant();
        if (lower.Contains("qwen"))
        {
            return ($"<|im_start|>user\n{instruction}<|im_end|>\n<|im_start|>assistant\n", ["<|im_end|>", "<|endoftext|>", "\n", "\r"]);
        }
        if (lower.Contains("gemma"))
        {
            return ($"<start_of_turn>user\n{instruction}<end_of_turn>\n<start_of_turn>model\n", ["<end_of_turn>", "<eos>", "\n", "\r"]);
        }
        if (lower.Contains("llama-3") || lower.Contains("llama3"))
        {
            return ($"<|begin_of_text|><|start_header_id|>user<|end_header_id|>\n\n{instruction}<|eot_id|><|start_header_id|>assistant<|end_header_id|>\n\n", ["<|eot_id|>", "<|end_of_text|>", "\n", "\r"]);
        }
        return ($"<|user|>{instruction}</s><|assistant|>", ["</s>", "<|user|>", "<|system|>", "<|im_end|>", "\n", "\r"]);
    }

    [GeneratedRegex(@"<think>.*?</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ThinkTagRegex();

    internal static string CleanOutput(string raw, TranslationTarget target)
    {
        var text = ThinkTagRegex().Replace(raw, "")
            .Replace("</s>", "", StringComparison.Ordinal)
            .Replace("<s>", "", StringComparison.Ordinal)
            .Replace("<|assistant|>", "", StringComparison.Ordinal)
            .Replace("<|im_end|>", "", StringComparison.Ordinal)
            .Replace("<|endoftext|>", "", StringComparison.Ordinal)
            .Replace("<end_of_turn>", "", StringComparison.Ordinal)
            .Replace("<|eot_id|>", "", StringComparison.Ordinal)
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

    private static void DeletePidFile(string? pidFile)
    {
        if (pidFile is null) return;
        try { File.Delete(pidFile); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _idleTimer?.Dispose();
        _disposeCts.Cancel();
        lock (_stateGate)
        {
            _pendingCts?.Cancel();
            StopServerLocked(clearCache: true);
        }
        _inferGate.Dispose();
        _disposeCts.Dispose();
        _http.Dispose();
    }
}
