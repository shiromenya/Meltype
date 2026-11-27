// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 shiromenya

using Meltype.Input;
using Meltype.Learning;
using static Meltype.Tests.CompositionTests;

namespace Meltype.Tests;

internal static class TimestampConversionTests
{
    private static readonly DateTime FixedUtcNow = new(2026, 11, 27, 12, 23, 35, DateTimeKind.Utc);

    [Test]
    public static void Timestamp_BareKeywordsAppendCandidatesAndPreserveNormalConversion()
    {
        var k = new Keyboard(now: () => FixedUtcNow);
        k.Type("iso ");
        Assert.Equal("いそ", k.Host.View!.Candidates[0], "裸の iso は通常の変換候補を先に残す");
        Assert.True(k.Host.View.Candidates.Contains("2026-11-27T12:23:35Z"), string.Join(",", k.Host.View.Candidates));
        Assert.True(k.Host.View.Candidates.Contains("1795782215"), string.Join(",", k.Host.View.Candidates));

        var k2 = new Keyboard(now: () => FixedUtcNow);
        k2.Type("taimusutanpu ");
        Assert.True(k2.Host.View!.Candidates.Contains("2026-11-27T12:23:35Z"), string.Join(",", k2.Host.View.Candidates));
        Assert.True(k2.Host.View.Candidates.Contains("1795782215"), string.Join(",", k2.Host.View.Candidates));
    }

    [Test]
    public static void Timestamp_EmptyFunctionGeneratesCurrentTimestampAndIsNotLearned()
    {
        var history = new ConversionHistory(null);
        var k = new Keyboard(history: history, now: () => FixedUtcNow);
        k.Type("iso() ");
        Assert.Equal("2026-11-27T12:23:35+00:00", k.Host.View!.Text);
        Assert.True(k.Host.View.Candidates.Contains("2026-11-27T12:23:35Z"), string.Join(",", k.Host.View.Candidates));
        Assert.True(k.Host.View.Candidates.Contains("1795782215"), string.Join(",", k.Host.View.Candidates));
        k.Press(VirtualKeys.Return);
        Assert.Equal("2026-11-27T12:23:35+00:00", k.Host.Document);
        Assert.True(history.Get("iso()") is null, "時刻変換は学習しない");

        var kUnix = new Keyboard(now: () => FixedUtcNow);
        kUnix.Type("UNIX() ");
        Assert.Equal("1795782215", kUnix.Host.View!.Text, "大文字 UNIX() は UNIX 秒を先頭にする");
        Assert.True(kUnix.Host.View.Candidates.Contains("1795782215000"), string.Join(",", kUnix.Host.View.Candidates));
    }

    [Test]
    public static void Timestamp_ConvertsBetweenUnixAndDateTime()
    {
        var k = new Keyboard(now: () => FixedUtcNow);
        k.Type("unix(1795782215) ");
        Assert.Equal("2026-11-27 12:23:35", k.Host.View!.Text);
        Assert.True(k.Host.View.Candidates.Contains("2026-11-27T12:23:35Z"), string.Join(",", k.Host.View.Candidates));

        var kMs = new Keyboard(now: () => FixedUtcNow);
        kMs.Type("epoch(1795782215000) ");
        Assert.Equal("2026-11-27 12:23:35", kMs.Host.View!.Text, "13 桁はミリ秒として解釈する");

        var kDate = new Keyboard(now: () => FixedUtcNow);
        kDate.Type("unix(2026-11-27T12:23:35Z) ");
        Assert.Equal("1795782215", kDate.Host.View!.Text, "ISO 8601 日時を UNIX 秒へ変換する");
        Assert.True(kDate.Host.View.Candidates.Contains("1795782215000"), string.Join(",", kDate.Host.View.Candidates));
    }

    [Test]
    public static void Timestamp_SupportsTabPrediction()
    {
        var k = new Keyboard(predictor: new Predictor(null, () => [], null), now: () => FixedUtcNow);
        k.Type("unix(1795782215)");
        k.Press(VirtualKeys.Tab);
        Assert.Equal("2026-11-27 12:23:35", k.Host.View!.Text, "予測変換 (Tab) でも確定できる");
        k.Press(VirtualKeys.Return);
        Assert.Equal("2026-11-27 12:23:35", k.Host.Document);
    }
}
