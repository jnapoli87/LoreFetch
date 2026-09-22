using System.Diagnostics;
using LoreFetch.Capture;
using LoreFetch.Core.Abstractions;
using Xunit;

namespace LoreFetch.Tests.StreamC.Hardware;

/// C5's whole contract, proven against real hardware: an unplug must
/// surface as a clean `FrameSourceException` from `FrameWatchdog` — never a
/// hang, never any other exception type — within roughly
/// `ScanSettings.FrameWatchdogMs` of the last frame, and `DisposeAsync`
/// must still complete afterward.
///
/// Kept in its own class, separately traited `[Trait("Interactive","Unplug")]`
/// on top of `[Trait("Category","Hardware")]`, so a normal
/// `Category=Hardware` hardware run does not sit blocked waiting for a
/// human to physically unplug a camera — see docs/stream-c-capture.md C5
/// and this stream's own "Done when": "Unplugging the camera mid-run
/// produces a clean error." The other four hardware tests are runnable
/// unattended (memory, latency, negotiation, reopen); this one genuinely
/// needs a person at the machine.
[Trait("Category", "Hardware")]
[Trait("Interactive", "Unplug")]
public sealed class UnplugCameraTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly HardwareLogging _logging;

    public UnplugCameraTests(ITestOutputHelper output)
    {
        _output = output;
        var dir = HardwareTestSupport.ResolveOutputDirectory();
        output.WriteLine($"Hardware test output directory: {dir}");
        _logging = HardwareTestSupport.CreateLogging(output, GetType().Name, dir);
    }

    public void Dispose() => _logging.Dispose();

    [Fact]
    public async Task UnplugProducesCleanErrorWithinWatchdog()
    {
        var waitSeconds = HardwareTestSupport.GetEnvInt("LOREFETCH_HW_UNPLUG_WAIT_S", 120);
        var settings = new ScanSettings();
        var factory = new WebcamFrameSourceFactory(_logging.Factory);

        var source = await factory.CreateAsync(settings, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        var disposed = false;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(waitSeconds) + TimeSpan.FromSeconds(30));
            var enumerator = source.ReadAsync(cts.Token).GetAsyncEnumerator(cts.Token);
            var armedAt = Stopwatch.StartNew();
            var announced = false;
            DateTimeOffset lastFrameAt = default;

            try
            {
                while (true)
                {
                    // C4-fix: the per-call bound must be at least the
                    // product's own watchdogs plus margin (see
                    // HardwareTestSupport.ComputeMoveNextBound) — the old
                    // `waitSeconds + 10` constant already exceeded that in
                    // practice at the default waitSeconds=120, but if
                    // LOREFETCH_HW_UNPLUG_WAIT_S is set low it could have
                    // raced FrameWatchdog exactly like
                    // Negotiates1080pMjpgAndDeliversLiveFrames did, and
                    // masked the result the same way at dispose.
                    // `minimumBound` keeps the human-unplug allowance.
                    if (!await HardwareTestSupport.MoveNextWithHarnessBoundAsync(
                            enumerator,
                            settings,
                            cts,
                            TestContext.Current.CancellationToken,
                            minimumBound: TimeSpan.FromSeconds(waitSeconds + 10)))
                    {
                        Assert.Fail("Enumerator completed without throwing — expected FrameSourceException after the unplug.");
                    }

                    lastFrameAt = DateTimeOffset.UtcNow;
                    enumerator.Current.Dispose();

                    if (!announced && armedAt.Elapsed >= TimeSpan.FromSeconds(5))
                    {
                        announced = true;
                        _output.WriteLine("");
                        _output.WriteLine("=========================================");
                        _output.WriteLine("=====  UNPLUG THE CAMERA NOW  ===========");
                        _output.WriteLine("=========================================");
                        _output.WriteLine($"(waiting up to {waitSeconds}s for FrameWatchdog to notice)");
                    }
                }
            }
            catch (FrameSourceException ex)
            {
                var elapsedSinceLastFrame = DateTimeOffset.UtcNow - lastFrameAt;
                _output.WriteLine($"Caught FrameSourceException {elapsedSinceLastFrame.TotalMilliseconds:F0} ms after the last frame " +
                                   $"(FrameWatchdogMs = {settings.FrameWatchdogMs}).");
                _output.WriteLine($"Message: {ex.Message}");
            }
            finally
            {
                await enumerator.DisposeAsync();
            }

            var disposeSw = Stopwatch.StartNew();
            await source.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            disposeSw.Stop();
            disposed = true;
            _output.WriteLine($"DisposeAsync completed in {disposeSw.Elapsed.TotalMilliseconds:F0} ms.");
            Assert.True(disposeSw.Elapsed < TimeSpan.FromSeconds(5), $"Expected DisposeAsync to complete within 5s; took {disposeSw.Elapsed.TotalSeconds:F1}s.");
        }
        finally
        {
            if (!disposed)
            {
                // Best-effort — if the assertions above already failed we
                // still don't want to leak the device handle for whatever
                // test runs next.
                try
                {
                    await source.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                }
                catch
                {
                    // Already reporting a failure; this is cleanup only.
                }
            }
        }
    }
}
