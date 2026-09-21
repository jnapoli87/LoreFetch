using LoreFetch.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace LoreFetch.Core.Scanning;

/// Constructs the pipeline. Owned and written by Stream 0, so the wiring
/// recipe is frozen in one place and the App only calls it — the App is the
/// composition root, but it does not know the wiring (CONTRACTS.md
/// "Composition"). `ScanPipeline`'s own constructor stays public only so
/// tests can build it directly against the fakes; this is the blessed path
/// for everything else.
public static class ScanPipelineFactory
{
    public static IScanPipeline Create(
        IFrameSource source,
        ICardDetector detector,
        IRectifier rectifier,
        ICardIdentifier identifier,
        IAutoCaptureTrigger trigger,
        ScanSettings settings,
        ILoggerFactory loggers)
    {
        ArgumentNullException.ThrowIfNull(loggers);

        var logger = loggers.CreateLogger<ScanPipeline>();
        return new ScanPipeline(source, detector, rectifier, identifier, trigger, settings, logger);
    }
}
