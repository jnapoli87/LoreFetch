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
using Avalonia.VisualTree;
using LoreFetch.App.Diagnostics;
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
    /// WriteableBitmap-behind-Image ceiling (docs/design/app.md A1,
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
    // RowBytes itself ever changes for the same size — docs/design/app.md
    // notes RowBytes comes from SKImageInfo.RowBytes on the Skia backend and
    // is unpadded there, but nothing here assumes that holds everywhere.
    private byte[]? _convertedBuffer;
    private int _convertedBufferRowBytes;

    private long _renderScheduled;
    private DateTime _lastRenderRequestUtc = DateTime.MinValue;

    private IScanPipeline? _pipeline;

    // A10-prep item 5: fps counters. Null when the session didn't supply one
    // (e.g. tests that construct AppSession directly) — recording is then
    // simply skipped everywhere below.
    private PreviewDiagnostics? _diagnostics;

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
        _diagnostics = session.Diagnostics;
        _pipeline.FrameProcessed += OnFrameProcessed;

        // A7: AutoCaptured fires on the pipeline's background thread;
        // marshal to the UI thread before touching the cohort grid.
        _pipeline.AutoCaptured += OnAutoCaptured;

        // A9: SourceFailed fires on the pipeline's background thread
        // immediately before RunAsync faults. Marshal to the UI thread
        // and show the error banner — Message only, never a stack trace.
        _pipeline.SourceFailed += OnSourceFailed;

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
        // Direct|Bubble, which would miss the tunnel pass (docs/design/app.md
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
            _pipeline.SourceFailed -= OnSourceFailed;  // A9
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
            // A9: show the store-lock banner. Refresh is called on the UI
            // thread so we can set StoreLockMessage directly.
            if (DataContext is ViewModels.MainViewModel vm)
            {
                vm.StoreLockMessage =
                    "The collection file is open in another program (e.g. Excel). " +
                    "Close it and click Retry.";
            }
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
            // A9: export reads from the store (ListAsync) before writing — a
            // CollectionStoreException means the store file is locked. Show
            // the same retry banner as the Enter-commit path. Marshal to UI
            // because ExportToStreamAsync uses ConfigureAwait(false).
            Dispatcher.UIThread.Post(() =>
            {
                if (DataContext is ViewModels.MainViewModel vm)
                    vm.StoreLockMessage =
                        "The collection file is open in another program (e.g. Excel). " +
                        "Close it and click Retry.";
            });
        }
        catch (OperationCanceledException)
        {
            // Window closing during export — ignore.
        }
        catch (IOException)
        {
            // Disk write error — not a CollectionStoreException; no retry banner.
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

        // A10-prep item 5: "pipeline frames processed per second" — every
        // frame the pipeline hands the UI, independent of whether the ~15 fps
        // gate below actually renders it.
        _diagnostics?.RecordFrameProcessed();

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
    /// (docs/design/app.md A1).
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

            // A10-prep item 5: "rendered preview fps" — counted HERE, after
            // the bitmap lock is disposed and the blit has actually happened,
            // never in OnFrameProcessed. The ~15 fps gate above means most
            // processed frames never reach this line, which is the whole
            // point of tracking the two rates separately (see
            // PreviewDiagnostics's own doc comment).
            _diagnostics?.RecordFrameRendered();

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
    /// using `QuadOverlayCanvas`'s own rendered bounds — NOT `PreviewImage`'s
    /// (A10 known bug 3, fixed): with `Stretch="Uniform"`, Avalonia's `Image`
    /// arrange rect is ALREADY the tightly-fit, centred content rectangle —
    /// its own `Bounds.Size` is the post-letterbox size and `Bounds.Position`
    /// is the centring offset, not the full cell. Feeding that size into
    /// `FrameToControlTransform.Compute` re-applies the SAME uniform-fit math
    /// to an already-fitted rect (which is why it silently computed near-1.0
    /// scale and near-zero offset), and then discarding `Bounds.Position`
    /// throws away the one number that mattered: the actual centring offset
    /// where the image content really sits. The quads were drawn relative to
    /// `QuadOverlayCanvas`'s own origin (the FULL cell's top-left), so they
    /// landed at the top/left of the whole preview pane instead of on the
    /// letterboxed image — reported as "the overlay sits ~35 px too high."
    /// `QuadOverlayCanvas` is a plain `Canvas` filling the full cell (it has
    /// no Stretch-mode arrange logic of its own), so ITS `Bounds` is the
    /// outer container `FrameToControlTransform.Compute` expects, and its
    /// coordinate origin is exactly the one the drawn `Polygon` points use —
    /// which is what makes this the correct fix rather than another
    /// coordinate-space mismatch. The canvas is cleared and rebuilt each call
    /// rather than diffed — at ~15 fps and at most a handful of quads, a
    /// handful of small control allocations costs nothing next to the bitmap
    /// blit above it.
    private void DrawQuadOverlay(IReadOnlyList<CardQuad> quads, int frameWidth, int frameHeight)
    {
        QuadOverlayCanvas.Children.Clear();

        var bounds = QuadOverlayCanvas.Bounds;
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
    // see docs/design/app.md §A6 for the reasoning (tunnel beats bubble, tunnel
    // handler beats KeyBindings). The focus bail is the critical correctness
    // check: without it, Space in the type-ahead TextBox is consumed by the
    // global handler, and "Black Lotus" becomes untypeable (WM_CHAR / AvnView
    // trap described in docs/design/app.md §A6).
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
    /// A9: Fired on the pipeline's background thread when the frame source
    /// fails (device unplugged, taken by another app, watchdog timeout).
    /// Marshals to the UI thread and shows the SourceFailed banner with
    /// <see cref="FrameSourceException.Message"/> only — never a stack trace.
    /// </summary>
    private void OnSourceFailed(FrameSourceException ex)
    {
        var message = ex.Message; // capture before marshalling (ex may be GC'd)
        Dispatcher.UIThread.Post(() =>
        {
            if (DataContext is ViewModels.MainViewModel vm)
                vm.SourceErrorMessage = message;
        });
    }

    /// <summary>
    /// A9: "Retry" button on the store-lock banner. Re-invokes
    /// <see cref="OnEnterAsync"/> which attempts the pending commit again.
    /// On success the banner is cleared automatically; on another failure
    /// the banner message is refreshed.
    /// </summary>
    private void OnStoreLockRetryClick(object? sender, RoutedEventArgs e)
    {
        _ = OnEnterAsync();
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
    /// leaves the grid intact so the user can retry; A9 adds the visible
    /// retry banner (StoreLockMessage).
    /// </summary>
    /// <remarks>
    /// A10-fix bug 2: a successful commit used to leave <c>CollectionGrid</c>
    /// showing whatever it last held — the row WAS written (the manual
    /// Refresh button always proved that), but nothing told
    /// <see cref="CollectionViewModel"/> to re-read the store. Reload only
    /// runs here, in the success path: never on capture (Space writes
    /// nothing — see <see cref="MainViewModel.CaptureFromPipelineAsync"/>,
    /// which never touches the store) and never in the
    /// <see cref="CollectionStoreException"/> branch below (nothing
    /// committed there either — the retry banner + retained cohort are the
    /// correct response, not a reload of an unchanged file).
    /// </remarks>
    private async Task OnEnterAsync()
    {
        if (DataContext is not ViewModels.MainViewModel vm) return;

        try
        {
            // CommitCohortAsync may do real I/O in the live store (CSV write +
            // temp-rename). ConfigureAwait(false) keeps that off the UI thread.
            await vm.CommitCohortAsync(_cts.Token).ConfigureAwait(false);

            // Success: clear the pending cohort and any retry banner, then
            // reload the collection view — all on the UI thread.
            Dispatcher.UIThread.Post(() =>
            {
                vm.ClearPendingCohort();
                vm.StoreLockMessage = null; // A9: clear the retry banner

                // A10-fix bug 2: fire-and-forget is fine here — RefreshCollectionAsync
                // already catches CollectionStoreException (shows the retry banner)
                // and OperationCanceledException (window closing) itself.
                _ = RefreshCollectionAsync();
            });
        }
        catch (CollectionStoreException)
        {
            // A7 contract: do not crash; keep the pending cohort intact so
            // the user can retry after closing the blocking process (e.g.
            // Excel holding the CSV).
            // A9: show the retry banner — Message only, no stack trace.
            const string msg =
                "The collection file is open in another program (e.g. Excel). " +
                "Close it and click Retry.";
            Dispatcher.UIThread.Post(() =>
            {
                vm.StoreLockMessage = msg;
            });
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
    /// <remarks>
    /// A10-fix bug 1 ("Set card manually…" did nothing in the live exe):
    /// this used to read <c>((MenuItem)sender).Parent as ContextMenu</c> then
    /// <c>ContextMenu.PlacementTarget?.DataContext</c>. That only works when
    /// something calls <c>ContextMenu.Open(control)</c> explicitly —
    /// Avalonia's own native path for opening a <c>Control.ContextMenu</c> on
    /// a real right-click never sets <c>PlacementTarget</c>, so the
    /// null-conditional silently no-opped on every live right-click, even
    /// though the menu opened and the item's Click fired correctly. The
    /// MenuItem's own <see cref="StyledElement.DataContext"/> is inherited
    /// through the logical tree from the tile Grid regardless of how the
    /// menu was opened, so reading it directly off <paramref name="sender"/>
    /// works both for a native right-click and for a menu opened
    /// programmatically (as the headless tests do).
    /// </remarks>
    private void OnTileSetManuallyClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: TileViewModel vm })
        {
            vm.IsTypeAheadOpen = true;
        }
    }

    /// <summary>
    /// "Clear" context-menu item click → revert a manual pick to the hash's
    /// own proposal via <see cref="TileViewModel.ClearFromUi"/>.
    /// </summary>
    /// <remarks>
    /// Same fix as <see cref="OnTileSetManuallyClick"/> — reads the
    /// MenuItem's own inherited <c>DataContext</c> rather than
    /// <c>ContextMenu.PlacementTarget</c>, which Avalonia's native
    /// right-click path never sets.
    /// </remarks>
    private void OnTileClearClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { DataContext: TileViewModel vm })
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
    /// <remarks>
    /// A10-fix bug 1: also subscribes to <see cref="AvaloniaObject.PropertyChanged"/>
    /// so that whenever THIS box's own <see cref="Visual.IsVisibleProperty"/>
    /// flips true — i.e. every time "Set card manually…" opens it, not just
    /// the first time — the box is focused automatically. Without this, the
    /// overlay appeared (once the sender/DataContext fix above landed) but
    /// nothing moved keyboard focus into it, so a user's first keystroke
    /// after right-clicking went nowhere.
    /// </remarks>
    private void OnTypeAheadLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is AutoCompleteBox acb && acb.DataContext is TileViewModel vm)
        {
            acb.AsyncPopulator = vm.TypeAheadPopulator;
            acb.MinimumPrefixLength = 2;
            acb.MinimumPopulateDelay = TimeSpan.FromMilliseconds(150);
            acb.PropertyChanged += OnTypeAheadPropertyChanged;
        }
    }

    /// <summary>
    /// Focuses the type-ahead box's own inner <c>TextBox</c> part the moment
    /// it becomes visible. <see cref="AutoCompleteBox"/> is not itself a
    /// text-input focus target — its template hosts the actual editable
    /// <c>TextBox</c> — so focusing the box directly would leave keystrokes
    /// with nowhere to land; this walks the (already-realized, template
    /// already applied by the time <c>Loaded</c> fired) visual tree to find it.
    /// </summary>
    /// <remarks>
    /// A10 known bug 1 (deferred at A10-fix, fixed here): this call used to
    /// run SYNCHRONOUSLY, inline with the <c>PropertyChanged</c> notification
    /// that <c>IsTypeAheadOpen = true</c> triggers. That notification fires
    /// while <see cref="OnTileSetManuallyClick"/> — the <c>ContextMenu</c>
    /// item's own <c>Click</c> handler — is STILL on the call stack: a real
    /// right-click's <c>MenuItem.Click</c> is only step one of Avalonia's own
    /// menu-item-selection handling, which closes the popup and restores
    /// focus to whatever had it before the menu opened (the tile) right
    /// after <c>Click</c> returns. So the synchronous <c>Focus()</c> call
    /// here landed a moment BEFORE that close-time restoration ran, and was
    /// promptly stolen back — invisibly, because the box was already visible
    /// and nothing un-focused it in an obviously wrong way, it just never
    /// really had focus by the time the user could type. Headless tests that
    /// raise <c>Click</c> and then call <c>ContextMenu.Close()</c> explicitly
    /// (see <see cref="ManualSetContextMenuTests"/>) run that close step
    /// AFTER asserting focus, so they never observed the steal-back — which
    /// is exactly the "headless focus assertion passed while the live window
    /// failed" gap.
    ///
    /// Posting the focus at <see cref="DispatcherPriority.Input"/> — lower
    /// than the dispatcher's own default (<c>Normal</c>) — fixes it two ways
    /// at once: a <c>Post</c> always runs on a LATER dispatcher pass than the
    /// still-executing synchronous call stack, so it can no longer land
    /// before the popup finishes closing; and even if that close path is
    /// itself dispatched rather than synchronous, <c>Input</c> being lower
    /// priority than <c>Normal</c> means ours is serviced after it rather
    /// than racing it. The visual-tree walk is deferred along with the
    /// focus call (not just captured now and focused later) in case the
    /// FIRST time this fires the box's template hasn't produced its inner
    /// <c>TextBox</c> yet.
    /// </remarks>
    private void OnTypeAheadPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Visual.IsVisibleProperty || sender is not AutoCompleteBox { IsVisible: true } acb)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            // The box may have been hidden again (e.g. "Clear" or a fast
            // second right-click) by the time this runs — re-check rather
            // than blindly stealing focus for a closed overlay.
            if (!acb.IsVisible)
            {
                return;
            }

            var innerTextBox = acb.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
            if (innerTextBox is not null)
                innerTextBox.Focus();
            else
                acb.Focus();
        }, DispatcherPriority.Input);
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
