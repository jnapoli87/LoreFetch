using LoreFetch.Core.Abstractions;
using Xunit;

namespace LoreFetch.Tests.Integration.Unit;

public class ScanSettingsTests
{
    [Fact]
    public void Default_CameraRotationDegrees_Is90()
    {
        var settings = new ScanSettings();

        Assert.Equal(90, settings.CameraRotationDegrees);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void CameraRotationDegrees_AcceptsTheFourValidValues(int degrees)
    {
        var settings = new ScanSettings { CameraRotationDegrees = degrees };

        Assert.Equal(degrees, settings.CameraRotationDegrees);
    }

    [Theory]
    [InlineData(45)]
    [InlineData(-90)]
    [InlineData(360)]
    [InlineData(91)]
    public void CameraRotationDegrees_RejectsAnythingElse(int degrees)
    {
        var settings = new ScanSettings();

        Assert.Throws<ArgumentOutOfRangeException>(() => settings.CameraRotationDegrees = degrees);
    }
}
