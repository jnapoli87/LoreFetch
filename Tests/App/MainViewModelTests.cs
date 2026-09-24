using LoreFetch.App.ViewModels;
using LoreFetch.Core.Abstractions;
using Xunit;

namespace LoreFetch.Tests.App;

/// Unit tests for <see cref="MainViewModel"/> — the write-through invariant:
/// every setter must write the new value to the underlying
/// <see cref="ScanSettings"/> immediately, and the VM's initial state must
/// mirror whatever defaults <see cref="ScanSettings"/> came with.
///
/// Chaos-tested: removing the <c>_settings.ExpectedCount = value;</c> write
/// causes <c>ExpectedCount_Set_WritesToScanSettings</c> to fail with
/// "Expected: 3, Actual: 1" — the right location, the right value, the
/// right assertion.
/// Removing <c>_settings.AutoCaptureEnabled = value;</c> causes
/// <c>AutoCaptureEnabled_Set_WritesToScanSettings</c> to fail with
/// "Expected: True, Actual: False" — same fidelity.
public class MainViewModelTests
{
    // -----------------------------------------------------------------------
    // ExpectedCount write-through
    // -----------------------------------------------------------------------

    [Fact]
    public void ExpectedCount_Set_WritesToScanSettings()
    {
        var settings = new ScanSettings { ExpectedCount = 1 };
        var vm = new MainViewModel(settings);

        vm.ExpectedCount = 3;

        Assert.Equal(3, settings.ExpectedCount);
    }

    [Fact]
    public void ExpectedCount_SetToInvalidValue_DoesNotWriteToSettings()
    {
        var settings = new ScanSettings { ExpectedCount = 1 };
        var vm = new MainViewModel(settings);

        vm.ExpectedCount = 5; // not in {1, 3, 9} — silent no-op per spec

        Assert.Equal(1, settings.ExpectedCount);
    }

    [Fact]
    public void ExpectedCount_Set_WritesToScanSettings_AllValidValues()
    {
        foreach (var count in new[] { 1, 3, 9 })
        {
            var settings = new ScanSettings();
            var vm = new MainViewModel(settings);

            vm.ExpectedCount = count;

            Assert.Equal(count, settings.ExpectedCount);
        }
    }

    // -----------------------------------------------------------------------
    // IsCount helpers — derived boolean properties for RadioButton TwoWay binding
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(1, true, false, false)]
    [InlineData(3, false, true, false)]
    [InlineData(9, false, false, true)]
    public void IsCountHelpers_ReflectExpectedCount(int count, bool is1, bool is3, bool is9)
    {
        var vm = new MainViewModel(new ScanSettings());

        vm.ExpectedCount = count;

        Assert.Equal(is1, vm.IsCount1);
        Assert.Equal(is3, vm.IsCount3);
        Assert.Equal(is9, vm.IsCount9);
    }

    [Fact]
    public void IsCount3_SetTrue_SetsExpectedCountAndWritesToSettings()
    {
        var settings = new ScanSettings { ExpectedCount = 1 };
        var vm = new MainViewModel(settings);

        vm.IsCount3 = true;

        Assert.Equal(3, vm.ExpectedCount);
        Assert.Equal(3, settings.ExpectedCount);
    }

    [Fact]
    public void IsCount1_SetFalse_IsNoOp()
    {
        // Setting to false must not change ExpectedCount — only the competing
        // radio-button's "true" setter should change it, per spec.
        var settings = new ScanSettings { ExpectedCount = 1 };
        var vm = new MainViewModel(settings);

        vm.IsCount1 = false;

        Assert.Equal(1, settings.ExpectedCount);
    }

    // -----------------------------------------------------------------------
    // AutoCaptureEnabled write-through
    // -----------------------------------------------------------------------

    [Fact]
    public void AutoCaptureEnabled_SetTrue_WritesToScanSettings()
    {
        var settings = new ScanSettings { AutoCaptureEnabled = false };
        var vm = new MainViewModel(settings);

        vm.AutoCaptureEnabled = true;

        Assert.True(settings.AutoCaptureEnabled);
    }

    [Fact]
    public void AutoCaptureEnabled_SetFalse_WritesToScanSettings()
    {
        var settings = new ScanSettings { AutoCaptureEnabled = true };
        var vm = new MainViewModel(settings);

        vm.AutoCaptureEnabled = false;

        Assert.False(settings.AutoCaptureEnabled);
    }

    // -----------------------------------------------------------------------
    // Constructor — initial sync from ScanSettings
    // -----------------------------------------------------------------------

    [Fact]
    public void Constructor_SyncsInitialValuesFromSettings()
    {
        var settings = new ScanSettings { ExpectedCount = 9, AutoCaptureEnabled = true };

        var vm = new MainViewModel(settings);

        Assert.Equal(9, vm.ExpectedCount);
        Assert.True(vm.AutoCaptureEnabled);
    }

    [Fact]
    public void Constructor_DefaultSettings_MatchDefaultScanSettings()
    {
        var settings = new ScanSettings(); // ExpectedCount=1, AutoCaptureEnabled=false per spec
        var vm = new MainViewModel(settings);

        Assert.Equal(1, vm.ExpectedCount);
        Assert.False(vm.AutoCaptureEnabled);
    }
}
