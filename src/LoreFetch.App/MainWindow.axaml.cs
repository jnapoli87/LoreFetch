using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using LoreFetch.App.ViewModels;
using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Scanning;

namespace LoreFetch.App;

/// The app's single window. A1 gave it the status line and the composition
/// hookup; A2 adds the live preview — one reused `WriteableBitmap`, filled
/// from `IScanPipeline.FrameProcessed` frames and blitted at a throttled
/// ~15 fps. The quad overlay, expected-count selector and cohort grid land
/// in later packages.
public partial class MainWindow : Window
{
    /// ~15 fps: plenty for the eye, and comfortably inside every measured
    /// WriteableBitmap-behind-Image ceiling (docs/stream-a-ui.md A1,
    /// "Corrected" — nothing here claims a number was ever measured beyond
    /// what's cited there, and 15 is well inside it). This only gates how
    /// OFTEN a render is requested — TopLevel.RequestAnimationFrame is
    /// itself render-loop-paced on top of that.
    private static readonly TimeSpan RenderInterval = TimeSpan.FromSeconds(1.0 / 15.0);

    // Fixes the A2 data race: the old design copied into a single
    // `_stagingBuffer` under a lock and let the UI thread convert from a
    // REFERENCE to that same array after releasing the lock, so the
    // producer could overwrite it mid-conversion (a torn frame — top of one
    // frame, bottom of the next). `FrameHandoff` is a three-slot rotation
    // where the producer never writes into a slot the consumer currently
    // holds, so that read is now impossible by construction. See its own
    // doc comment for the invariant. CONTRACTS.md guarantees a frame's own
    // contents are valid only for the duration of the FrameProcessed
    // callback, so `OnFrameProcessed` still does all its copying (via
    // `Publish`) before returning — conversion still happens later, on the
    // UI thread in OnRenderFrame.
    private readonly FrameHandoff _frameHandoff = new();
    private long _frameSequence;

    // Fires once, on the UI thread, when the ~15 fps gate defers a render
    // rather than dropping it (see ScheduleRenderIfDue). Not a
    // DispatcherTimer because it is armed from the background thread.
    private readonly Timer _deferredRenderTimer;

    // --- UI-thread-only fields --------------------------------------------
    //
    // ONE WriteableBitmap for the life of the app (A2). Reallocated only
    // when the frame's Width/Height changes — never per frame, which is the
    // per-frame-allocation trap CLAUDE.md's "Avalonia preview" note warns
    // about ("the documented cause of every 'camera preview is choppy'
    // report").
    private WriteableBitmap? _bitmap;
    private PixelSize _bitmapSize;

    // Reused conversion output, shaped to the CURRENT bitmap's
    // Lock().RowBytes * Height. Reallocated alongside `_bitmap`, and also if
    // RowBytes itself ever changes for the same size — docs/stream-a-ui.md
    // notes RowBytes comes from SKImageInfo.RowBytes on the Skia backend and
    // is unpadded there, but nothing here assumes that holds everywhere.
    private byte[]? _convertedBuffer;
    private int _convertedBufferRowBytes;

    private long _renderScheduled;
    private DateTime _lastRenderRequestUtc = DateTime.MinValue;

    private IScanPipeline? _pipeline;

    // A8: collection view model — always non-null; initialised with empty state
    // in the parameterless ctor, replaced with the session-wired instance in
    // the session ctor. CollectionPanel.DataContext is set to this so bindings
    // do not inherit the window's MainViewModel DataContext.
    private CollectionViewModel _collectionVm;

    // A7: cancelled in OnClosed to signal any in-flight CaptureAsync or
    // CommitCohortAsync that the window is shutting down.
    private readonly CancellationTokenSource _cts = new();

    // Parameterless constructor required by the Avalonia XAML previewer and
    // the compiled-XAML loader.
    public MainWindow()
    {
        InitializeComponent();

        // Dormant until ScheduleRenderIfDue's else-branch first arms it
        // with Change(...); Timeout.Infinite means it never fires on its
        // own. The callback runs on a thread-pool thread, so it still has
        // to marshal to the UI thread itself.
        _deferredRenderTimer = new Timer(
            _ => Dispatcher.UIThread.Post(RequestRenderNow),
            state: null,
            dueTime: Timeout.Infinite,
            period: Timeout.Infinite);

        // A8: initialise with an empty collection VM so CollectionPanel
        // has a non-null DataContext even before a session is set (e.g.
        // the XAML previewer). Prevents DataContext inheritance from the
        // window's MainViewModel, which does not have Rows / ExporterItems.
        _collectionVm = new CollectionViewModel();
        CollectionPanel.DataContext = _collectionVm;
    }

    public MainWindow(AppSession session)
        : this()
    {
        ArgumentNullException.ThrowIfNull(session);

        // The ONE place SourceDescription is read — never composed from
        // parts, per the global A override.
        StatusText.Text = session.Pipeline.SourceDescription;

        _pipeline = session.Pipeline;
        _pipeline.FrameProcessed += OnFrameProcessed;

        // A7: AutoCaptured fires on the pipeline's background thread;
        // marshal to the UI thread before touching the cohort grid.
        _pipeline.AutoCaptured += OnAutoCaptured;

        Closed += OnClosed;

        // A4: wire the expected-count selector and auto-capture toggle
        // through the first view model. DataContext is set here so the
        // AXAML RadioButton / CheckBox bindings find MainViewModel via
        // the standard Avalonia binding path. The VM holds a reference to
        // ScanSettings and writes through on each property change.
        // A6: pass the oracle catalog so each tile's type-ahead can search it.
        // A7: pass the store so the commit path has something to write to.
        DataContext = new MainViewModel(session.Settings, session.Catalog, session.Store);

        // A8: replace the empty-state collection VM with one wired to the
        // session's store and exporter list. Keeps CollectionPanel.DataContext
        // separate from the window's DataContext (which is MainViewModel).
        _collectionVm = new CollectionViewModel(session.Store, session.Exporters);
        CollectionPanel.DataContext = _collectionVm;

        // A7: window-level tunnel handler for Space/Enter/Escape. Must use
        // RoutingStrategies.Tunnel explicitly — AddHandler's default is
        // Direct|Bubble, which would miss the tunnel pass (stream-a-ui.md
        // §A6, keyboard-handling warning). The tunnel handler runs
        // root→target before the bubble pass, so it sees the key first.
        // Do NOT add KeyBindings for these keys — KeyboardDevice walks
        // ancestors' KeyBindings before the routed event even fires, so
        // mixing KeyBindings with the tunnel handler would double-fire.
        AddHandler(InputElement.KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_pipeline is not null)
        {
            _pipeline.FrameProcessed -= OnFrameProcessed;
            _pipeline.AutoCaptured -= OnAutoCaptured;  // A7
            _pipeline = null;
        }

        _deferredRenderTimer.Dispose();

        // A7: cancel any in-flight CaptureAsync / CommitCohortAsync then
        // dispose the source of the token. Order matters: Cancel before Dispose
        // so that tasks holding the token observe cancellation rather than an
        // ObjectDisposedException on token access.
        _cts.Cancel();
        _cts.Dispose();
    }

    // -----------------------------------------------------------------------
    // A8: Collection refresh and export handlers
    // -----------------------------------------------------------------------

    /// <summary>
    /// "Refresh" button click — reloads the collection rows from the store.
    /// </summary>
    private void OnRefreshCollectionClick(object? sender, RoutedEventArgs e)
    {
        _ = RefreshCollectionAsync();
    }

    private async Task RefreshCollectionAsync()
    {
        try
        {
            // LoadAsync is awaited on the UI thread so its continuation
            // (ObservableCollection mutations) stays on the UI thread.
            await _collectionVm.LoadAsync(_cts.Token);
        }
        catch (CollectionStoreException)
        {
            // A8 happy-path: just don't crash. A9 adds the retry banner.
        }
        catch (OperationCanceledException)
        {
            // Window closing while refresh was in flight — ignore.
        }
    }

    /// <summary>
    /// "Export…" button click — opens a save-file dialog and writes the
    /// collection via the selected exporter. The dialog supplies the stream;
    /// the actual write is delegated to
    /// <see cref="CollectionViewModel.ExportToStreamAsync"/> so tests can
    /// call that method directly with a <see cref="MemoryStream"/>.
    /// </summary>
    private void OnExportClick(object? sender, RoutedEventArgs e)
    {
        if (ExporterList.SelectedItem is not ExporterItem item) return;
        _ = ExportAsync(item.Exporter);
    }

    private async Task ExportAsync(ICollectionExporter exporter)
    {
        var topLevel = GetTopLevel(this);
        if (topLevel is null) return;

        IStorageFile? file;
        try
        {
            file = await topLevel.StorageProvider.SaveFilePickerAsync(
                new FilePickerSaveOptions
                {
                    SuggestedFileName = "collection",
                    DefaultExtension = exporter.Format.FileExtension.TrimStart('.'),
                    FileTypeChoices =
                    [
                        new FilePickerFileType(exporter.Format.DisplayName)
                        {
                            Patterns = ["*" + exporter.Format.FileExtension]
                        }
                    ]
                });
        }
        catch (OperationCanceledException) { return; }

        if (file is null) return;

        try
        {
            await using var stream = await file.OpenWriteAsync().ConfigureAwait(false);
            await _collectionVm.ExportToStreamAsync(exporter, stream, _cts.Token).ConfigureAwait(false);
        }
        catch (CollectionStoreException)
        {
            // A8 happy-path: just don't crash. A9 adds the error banner.
        }
        catch (OperationCanceledException)
        {
            // Window closing during export — ignore.
        }
        catch (IOException)
        {
            // Disk write error — A8 happy-path, ignore; A9 adds error UX.
        }
    }

    /// Raised on the pipeline's background thread for every processed frame
    /// (CONTRACTS.md `IScanPipeline.FrameProcessed`). `frame` is valid only
    /// for the duration of this call, so the only thing this does with it is
    /// hand its bytes to `_frameHandoff` before returning — no Avalonia
    /// object is touched here, and nothing here ever retains `frame` itself.
    /// `Publish` does the actual copy, into a buffer the UI thread can never
    /// be reading (see `FrameHandoff`'s doc comment).
    private void OnFrameProcessed(CameraFrame frame, DetectionSnapshot snapshot)
    {
        var sequence = Interlocked.Increment(ref _frameSequence);
        _frameHandoff.Publish(
            frame.Pixels.Span, frame.Width, frame.Height, frame.Stride, frame.Layout, snapshot.Quads, sequence);

        ScheduleRenderIfDue();
    }

    /// Coalesces to ~15 fps AND to at most one outstanding request. The
    /// frame source can call OnFrameProcessed far faster than 15 fps (a
    /// synthetic folder source on a short timer, or a real 30 fps camera),
    /// so both gates are needed: the CompareExchange stops a second ask
    /// piling up before the render loop gets to the first one, and the time
    /// check limits how often a render actually fires.
    ///
    /// The time check used to just `return` when a frame landed inside the
    /// interval — which drops it silently: if the source then stalls (or
    /// stops), nothing ever asks for a render again and the newest frame
    /// sits in `_frameHandoff` forever undrawn. It now DEFERS instead: arms
    /// `_deferredRenderTimer` for the remainder of the interval, so the
    /// render still happens even if no further frame ever arrives. Only one
    /// of {defer, immediate request} can be outstanding at a time — that is
    /// exactly what `_renderScheduled` already gates — so re-arming the
    /// timer on every call some frame source floods this with is harmless:
    /// later calls in the same interval see `_renderScheduled` already set
    /// and return immediately without touching the timer again.
    private void ScheduleRenderIfDue()
    {
        if (Interlocked.CompareExchange(ref _renderScheduled, 1, 0) != 0)
        {
            return;
        }

        var elapsed = DateTime.UtcNow - _lastRenderRequestUtc;
        if (elapsed >= RenderInterval)
        {
            // RequestRenderNow itself asserts UI-thread access
            // (Dispatcher.VerifyAccess via RequestAnimationFrame), so the
            // call has to be marshalled there even though this method runs
            // on the pipeline's background thread.
            Dispatcher.UIThread.Post(RequestRenderNow);
        }
        else
        {
            _deferredRenderTimer.Change(RenderInterval - elapsed, Timeout.InfiniteTimeSpan);
        }
    }

    /// Runs on the UI thread, either posted directly from
    /// `ScheduleRenderIfDue` or from `_deferredRenderTimer`'s callback (via
    /// its own `Dispatcher.UIThread.Post` — see the constructor). Whichever
    /// path got here, this is the one place that stamps
    /// `_lastRenderRequestUtc` and asks for the next animation frame.
    private void RequestRenderNow()
    {
        _lastRenderRequestUtc = DateTime.UtcNow;
        RequestAnimationFrame(OnRenderFrame);
    }

    /// Runs on the UI thread. Order matters and is exactly
    /// Lock() -> convert -> dispose the lock -> InvalidateVisual(): the
    /// Skia backend caches an SKImage snapshot that only the lock's Dispose
    /// invalidates, so invalidating before disposing (or holding the lock
    /// across InvalidateVisual) renders the FIRST frame forever
    /// (docs/stream-a-ui.md A1).
    private void OnRenderFrame(TimeSpan _)
    {
        Interlocked.Exchange(ref _renderScheduled, 0);

        if (!_frameHandoff.TryTake(out var buffer, out var metadata))
        {
            return;
        }

        try
        {
            EnsureBitmap(metadata.Width, metadata.Height);

            using (var framebuffer = _bitmap!.Lock())
            {
                EnsureConvertedBuffer(framebuffer.RowBytes, metadata.Height);
                PixelConvert.ToBgra32(
                    buffer.Span, metadata.Width, metadata.Height, metadata.Stride, metadata.Layout,
                    _convertedBuffer!, framebuffer.RowBytes);
                Marshal.Copy(_convertedBuffer!, 0, framebuffer.Address, framebuffer.RowBytes * metadata.Height);
            }

            PreviewImage.InvalidateVisual();

            DrawQuadOverlay(metadata.Quads, metadata.Width, metadata.Height);
        }
        finally
        {
            // Frees the slot for the producer to reuse — must run even if
            // conversion above throws, or the producer permanently loses a
            // buffer out of its three-slot rotation.
            _frameHandoff.Return();
        }
    }

    /// Draws each detected quad as a vector `Polygon` child of
    /// `QuadOverlayCanvas` — over the `Image`, never baked into its pixel
    /// buffer (A3). `quads` are in FRAME coordinates (CONTRACTS.md
    /// `CardQuad`), so they are mapped through `FrameToControlTransform`
    /// using `PreviewImage`'s own rendered bounds, which is what accounts
    /// for the `Stretch="Uniform"` letterboxing. The canvas is cleared and
    /// rebuilt each call rather than diffed — at ~15 fps and at most a
    /// handful of quads, a handful of small control allocations costs
    /// nothing next to the bitmap blit above it.
    private void DrawQuadOverlay(IReadOnlyList<CardQuad> quads, int frameWidth, int frameHeight)
    {
        QuadOverlayCanvas.Children.Clear();

        var bounds = PreviewImage.Bounds;
        if (frameWidth <= 0 || frameHeight <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        var transform = FrameToControlTransform.Compute(frameWidth, frameHeight, bounds.Width, bounds.Height);

        foreach (var quad in quads)
        {
            var polygon = new Polygon
            {
                Stroke = Brushes.LimeGreen,
                StrokeThickness = 2,
                Points =
                [
                    transform.Apply(quad.TL),
                    transform.Apply(quad.TR),
                    transform.Apply(quad.BR),
                    transform.Apply(quad.BL),
                ],
            };

            QuadOverlayCanvas.Children.Add(polygon);
        }
    }

    /// Reallocates the ONE `WriteableBitmap` only when the frame's
    /// Width/Height changes. Rebinding `Image.Source` here is the one
    /// legitimate exception to "never rebind Source" — it happens only
    /// alongside a genuinely new bitmap instance, never for the same
    /// instance on a per-frame basis.
    private void EnsureBitmap(int width, int height)
    {
        var size = new PixelSize(width, height);
        if (_bitmap is not null && _bitmapSize.Equals(size))
        {
            return;
        }

        _bitmap?.Dispose();
        _bitmap = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        _bitmapSize = size;
        PreviewImage.Source = _bitmap;
    }

    private void EnsureConvertedBuffer(int rowBytes, int height)
    {
        var needed = rowBytes * height;
        if (_convertedBuffer is not null && _convertedBufferRowBytes == rowBytes && _convertedBuffer.Length >= needed)
        {
            return;
        }

        _convertedBuffer = new byte[needed];
        _convertedBufferRowBytes = rowBytes;
    }

    // -----------------------------------------------------------------------
    // A7: Keyboard map and auto-capture wiring
    //
    // All three keys are handled at the window level via a tunnel handler —
    // see stream-a-ui.md §A6 for the reasoning (tunnel beats bubble, tunnel
    // handler beats KeyBindings). The focus bail is the critical correctness
    // check: without it, Space in the type-ahead TextBox is consumed by the
    // global handler, and "Black Lotus" becomes untypeable (WM_CHAR / AvnView
    // trap described in stream-a-ui.md §A6).
    // -----------------------------------------------------------------------

    /// <summary>
    /// Fired on the pipeline's background thread when auto-capture fires.
    /// Marshals to the UI thread before touching the tile grid.
    /// </summary>
    private void OnAutoCaptured(Cohort cohort)
    {
        // Same pattern as FrameProcessed: this arrives on a background thread,
        // so everything that touches a control must be posted to the UI thread.
        Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is ViewModels.MainViewModel vm)
                vm.LoadCohort(cohort);
        });
    }

    /// <summary>
    /// Window-level tunnel key handler for Space / Enter / Escape.
    /// Registered in the session ctor with <c>RoutingStrategies.Tunnel</c>
    /// so it runs before any focused control's bubble-phase handler.
    /// </summary>
    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        // ⚠ Focus bail — MUST come first and MUST NOT set e.Handled.
        // When a TextBox (e.g. the type-ahead AutoCompleteBox inner box) has
        // focus, return WITHOUT marking Handled. On Win32, a tunnel handler
        // that marks Space Handled suppresses the following WM_CHAR, so the
        // focused TextBox never receives the character — "Black Lotus" becomes
        // untypeable. The same path exists on macOS (AvnView.mm -keyDown:
        // returns early when user code handled the event). Reproducible in
        // headless tests on both platforms.
        if (FocusManager?.GetFocusedElement() is TextBox)
            return;

        switch (e.Key)
        {
            case Key.Space:
                e.Handled = true;
                if (DataContext is ViewModels.MainViewModel captureVm && _pipeline is not null)
                    _ = captureVm.CaptureFromPipelineAsync(_pipeline, _cts.Token);
                break;

            case Key.Enter:
                e.Handled = true;
                _ = OnEnterAsync();
                break;

            case Key.Escape:
                e.Handled = true;
                if (DataContext is ViewModels.MainViewModel escapeVm)
                    escapeVm.ClearPendingCohort();
                break;
        }
    }

    /// <summary>
    /// Commits the pending cohort off the UI thread, then clears the grid
    /// on the UI thread on success. On <see cref="CollectionStoreException"/>:
    /// leaves the grid intact so the user can retry (A9 adds the retry banner).
    /// </summary>
    private async Task OnEnterAsync()
    {
        if (DataContext is not ViewModels.MainViewModel vm) return;

        try
        {
            // CommitCohortAsync may do real I/O in the live store (CSV write +
            // temp-rename). ConfigureAwait(false) keeps that off the UI thread.
            await vm.CommitCohortAsync(_cts.Token).ConfigureAwait(false);

            // Success: clear the pending cohort on the UI thread.
            Dispatcher.UIThread.Post(vm.ClearPendingCohort);
        }
        catch (CollectionStoreException)
        {
            // A7 contract: do not crash; keep the pending cohort intact so
            // the user can retry after closing the blocking process (e.g.
            // Excel holding the CSV). A9 adds the retry dialog/banner.
        }
        catch (OperationCanceledException)
        {
            // Window closing while commit was in flight — ignore.
        }
    }

    // -----------------------------------------------------------------------
    // A6: Mouse-interaction handlers
    //
    // All handlers reach the TileViewModel through the control's DataContext
    // (for tile-level events) or through ContextMenu.PlacementTarget (for
    // ContextMenu item events), and then call the VM's own methods so the tile
    // state is never mutated directly from the view.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Left-click (Tapped) on a tile's outer Grid → toggle the X opt-out.
    /// </summary>
    private void OnTileTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Grid { DataContext: TileViewModel vm })
        {
            vm.ToggleExcludedFromUi();
        }
    }

    /// <summary>
    /// "Set card manually…" context-menu item click → open the type-ahead
    /// overlay by setting <see cref="TileViewModel.IsTypeAheadOpen"/> = true.
    /// </summary>
    private void OnTileSetManuallyClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi &&
            mi.Parent is ContextMenu cm &&
            cm.PlacementTarget?.DataContext is TileViewModel vm)
        {
            vm.IsTypeAheadOpen = true;
        }
    }

    /// <summary>
    /// "Clear" context-menu item click → revert a manual pick to the hash's
    /// own proposal via <see cref="TileViewModel.ClearFromUi"/>.
    /// </summary>
    private void OnTileClearClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi &&
            mi.Parent is ContextMenu cm &&
            cm.PlacementTarget?.DataContext is TileViewModel vm)
        {
            vm.ClearFromUi();
        }
    }

    /// <summary>
    /// Wires the <c>AutoCompleteBox</c>'s <c>AsyncPopulator</c> and
    /// per-control settings once it is loaded (XAML binding cannot reliably
    /// assign a delegate-type property). Called for every tile in the grid
    /// because each tile in the <c>ItemsControl</c> gets its own
    /// <c>AutoCompleteBox</c> instance from the <c>DataTemplate</c>.
    /// </summary>
    private void OnTypeAheadLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is AutoCompleteBox acb && acb.DataContext is TileViewModel vm)
        {
            acb.AsyncPopulator = vm.TypeAheadPopulator;
            acb.MinimumPrefixLength = 2;
            acb.MinimumPopulateDelay = TimeSpan.FromMilliseconds(150);
        }
    }

    /// <summary>
    /// AutoCompleteBox selection changed → apply the chosen entry via
    /// <see cref="TileViewModel.SetManuallyFromUi"/> and close the overlay.
    /// Fires for both selection and de-selection; guards against a null
    /// <c>SelectedItem</c> (de-selection sets it to null).
    /// </summary>
    private void OnTypeAheadSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is AutoCompleteBox acb &&
            acb.DataContext is TileViewModel vm &&
            acb.SelectedItem is CatalogItem item)
        {
            vm.SetManuallyFromUi(item.ToEntry());
            vm.IsTypeAheadOpen = false;
            acb.Text = string.Empty; // reset text for next use
        }
    }
}
