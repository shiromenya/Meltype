// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 shiromenya

using Meltype.Dictionary;

namespace Meltype.Composition;

/// <summary>スクリーンリーダー向けの選択候補・意味の読み上げテキストを組み立てる。</summary>
public static class CandidateAnnouncement
{
    public static string? Format(CompositionView view)
    {
        if (view.Predictions is { Count: > 0 } predictions && view.SelectedPrediction >= 0 && view.SelectedPrediction < predictions.Count)
        {
            var idx = view.SelectedPrediction;
            return $"予測候補 ({idx + 1}/{predictions.Count}): {predictions[idx]}";
        }

        if (view.Candidates is { Count: > 0 } candidates && view.SelectedIndex >= 0 && view.SelectedIndex < candidates.Count)
        {
            var idx = view.SelectedIndex;
            var text = $"候補 ({idx + 1}/{candidates.Count}): {candidates[idx]}";
            if (view.Notes is not null && idx < view.Notes.Count && view.Notes[idx].ToDisplayText() is { Length: > 0 } note)
                text += $"、{note}";
            if (!string.IsNullOrWhiteSpace(view.Meaning))
                text += $"、{view.Meaning}";
            return text;
        }

        return null;
    }
}
