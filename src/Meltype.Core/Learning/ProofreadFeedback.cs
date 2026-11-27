// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 shiromenya

namespace Meltype.Learning;

/// <summary>
/// 文章推敲の採用・却下回数をパターンごとに記録し、却下回数が採用回数を上回ったパターンを自動で抑止する。
/// 後から推敲候補を選び直して確定すれば採用回数が増え、抑止は自動で解除される。
/// </summary>
internal sealed class ProofreadFeedback
{
    private readonly Dictionary<string, (int Accepted, int Rejected)> _counts = new(StringComparer.Ordinal);

    /// <summary>このパターンが現在自動抑止中か (却下回数が採用回数を上回っている)。</summary>
    public bool IsBlocked(string pattern) =>
        _counts.TryGetValue(pattern, out var count) && count.Rejected > count.Accepted;

    /// <summary>推敲候補がそのまま確定されたことを記録する。</summary>
    public void RecordAccepted(IEnumerable<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            var current = _counts.GetValueOrDefault(pattern);
            _counts[pattern] = (current.Accepted + 1, current.Rejected);
        }
    }

    /// <summary>推敲候補が却下され、元の表現など別の候補が確定されたことを記録する。</summary>
    public void RecordRejected(IEnumerable<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            var current = _counts.GetValueOrDefault(pattern);
            _counts[pattern] = (current.Accepted, current.Rejected + 1);
        }
    }
}
