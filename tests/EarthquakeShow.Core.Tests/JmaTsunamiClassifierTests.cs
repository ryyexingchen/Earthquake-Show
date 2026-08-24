using EarthquakeShow.Core.Models;
using EarthquakeShow.Core.Services;
using Xunit;

namespace EarthquakeShow.Core.Tests;

public sealed class JmaTsunamiClassifierTests
{
    [Fact]
    public void Classify_KnownJmaCodeTakesPriorityOverText()
    {
        Assert.Equal(
            TsunamiLevel.NoConcern,
            JmaTsunamiClassifier.Classify("この地震による津波の心配はありません。", "0215"));
    }

    [Fact]
    public void Classify_GenericTemplateDoesNotBecomeMajorWarning()
    {
        Assert.Equal(
            TsunamiLevel.Investigating,
            JmaTsunamiClassifier.Classify(
                "津波警報等（大津波警報・津波警報あるいは津波注意報）",
                ""));
    }

    [Fact]
    public void Classify_UnknownNonEmptyCommentIsInvestigating()
    {
        Assert.Equal(
            TsunamiLevel.Investigating,
            JmaTsunamiClassifier.Classify("津波に関する情報を確認中。", "9999"));
    }

    [Fact]
    public void Classify_OfficialNoTsunamiTextIsNoConcern()
    {
        Assert.Equal(TsunamiLevel.NoConcern, JmaTsunamiClassifier.Classify("津波なし", null));
    }

    [Fact]
    public void Classify_OfficialMinorChangeTextIsMinorChange()
    {
        Assert.Equal(
            TsunamiLevel.MinorChange,
            JmaTsunamiClassifier.Classify("津波予報（若干の海面変動）", null));
    }

    [Fact]
    public void Classify_ReleaseTextDoesNotRemainWarning()
    {
        Assert.Equal(
            TsunamiLevel.Investigating,
            JmaTsunamiClassifier.Classify("津波注意報解除", null));
    }
}
