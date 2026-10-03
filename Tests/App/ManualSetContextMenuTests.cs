using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LoreFetch.App;
using LoreFetch.App.ViewModels;
using LoreFetch.Core.Abstractions;
using LoreFetch.Core.Scanning;
using Xunit;

namespace LoreFetch.Tests.App;

/// <summary>
/// A10-fix bug 1: "Set card manually…" did nothing in the live exe — no
/// type-ahead ever appeared, so <see cref="TileState.ManuallySet"/> could not
/// be reached by hand.
///
/// <para>
/// The existing A6 <c>TileContextMenu_CarriesTwoItems_SetManuallyAndClear</c>
/// test (<c>TileInteractionTests.cs</c>) only asserts the two
/// <see cref="MenuItem"/> headers exist — it never opens the menu and never
/// raises <c>Click</c>, so it could not have caught this. These tests drive
/// the REAL <see cref="MainWindow"/> the way a user does: a real right-click
/// (<c>MouseDown</c>/<c>MouseUp</c> with <see cref="MouseButton.Right"/>)
/// opens the tile's actual <see cref="ContextMenu"/>, and the real
/// <see cref="MenuItem.ClickEvent"/> is raised on the real menu item found in
/// that open menu's own <c>Items</c> — never a direct call into
/// <see cref="TileViewModel"/>.
/// </para>
///
/// <para>Chaos-test results are at the bottom of this file.</para>
/// </summary>
public class ManualSetContextMenuTests
{
    private const int Good = 100;
    private const int Ok = 200;

    private static RectifiedCard MakeCard() =>
        new([0, 0, 0, 255], stride: 4, PixelLayout.Bgra32, default);

    private static CardCandidate Candidate(string name, int distance) =>
        new("oracle-" + name, name, distance, ArtworkId: null);

    private static Cohort MakeSingleCohort()
    {
        var tile = new CohortTile(MakeCard(), [Candidate("Lightning Bolt", 50)], Good, Ok);
        return new Cohort(Guid.NewGuid(), DateTimeOffset.UtcNow, 1, CaptureReason.Manual, [tile]);
    }

    private static AppSession MakeSession(IOracleCatalog? catalog = null)
    {
        var settings = new ScanSettings();
        var pipeline = new NullScanPipeline();
        var source = new NullFrameSource();
        return new AppSession(pipeline, source, Task.CompletedTask, settings, catalog: catalog);
    }

    /// <summary>
    /// Finds the tile's outer Grid by its x:Name ("CohortTile" — not its
    /// literal Width/Height, which #17 will change), right-clicks it via the
    /// real headless pointer pipeline to open its ContextMenu, finds the real
    /// "Set card manually…" MenuItem inside that open menu and raises its
    /// real Click event — then asserts the type-ahead AutoCompleteBox becomes
    /// visible AND focused, and that choosing an entry sets the tile to
    /// ManuallySet.
    /// </summary>
    [AvaloniaFact]
    public void RightClick_SetCardManually_OpensFocusedTypeAhead_AndSelectionSetsManuallySet()
    {
        var catalog = new InlineOracleCatalog([new OracleEntry("oracle-lotus", "Black Lotus")]);
        var session = MakeSession(catalog);
        var window = new MainWindow(session);
        window.Width = 1024;
        window.Height = 768;

        var vm = (MainViewModel)window.DataContext!;
        vm.LoadCohort(MakeSingleCohort());

        window.Show();
        Dispatcher.UIThread.RunJobs();

        var tileGrid = window.GetVisualDescendants()
            .OfType<Grid>()
            .FirstOrDefault(g => g.Name == "CohortTile");
        Assert.NotNull(tileGrid);

        var contextMenu = tileGrid!.ContextMenu;
        Assert.NotNull(contextMenu);

        // Real right-click at the tile's centre, exactly as a user would.
        var center = tileGrid.TranslatePoint(
            new Point(tileGrid.Bounds.Width / 2, tileGrid.Bounds.Height / 2), window);
        Assert.NotNull(center);

        window.MouseDown(center!.Value, MouseButton.Right);
        window.MouseUp(center!.Value, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();

        Assert.True(contextMenu!.IsOpen, "A real right-click must open the tile's ContextMenu.");

        var menuItem = contextMenu.Items
            .OfType<MenuItem>()
            .FirstOrDefault(mi => mi.Header?.ToString() == "Set card manually…");
        Assert.NotNull(menuItem);

        // Raise the REAL Click routed event on the REAL MenuItem instance —
        // exactly what clicking it fires — never TileViewModel directly. A
        // real pointer click also closes the popup as part of Avalonia's own
        // menu-item selection handling (separate from the Click event this
        // raises); do that explicitly here so the screenshot below shows the
        // tile the way a user actually sees it post-click, not mid-click
        // with the popup still covering it.
        menuItem!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        contextMenu.Close();
        Dispatcher.UIThread.RunJobs();

        var tileVm = vm.Tiles[0];
        Assert.True(tileVm.IsTypeAheadOpen, "IsTypeAheadOpen must be true after clicking 'Set card manually…'.");

        var autoCompleteBox = window.GetVisualDescendants()
            .OfType<AutoCompleteBox>()
            .FirstOrDefault(a => a.DataContext == tileVm);
        Assert.NotNull(autoCompleteBox);
        Assert.True(autoCompleteBox!.IsVisible, "The type-ahead AutoCompleteBox must be visible.");

        // Focus: the box (or its internal TextBox) must actually have focus
        // so the user can type immediately, with no extra click.
        var focused = window.FocusManager?.GetFocusedElement() as Visual;
        var focusedInBox = focused is not null &&
            (ReferenceEquals(focused, autoCompleteBox) ||
             focused.GetVisualAncestors().Contains(autoCompleteBox));
        Assert.True(focusedInBox, $"Focus must land in the type-ahead box; actual focus: {focused?.GetType().Name ?? "null"}");

        // Screenshot the open, focused type-ahead overlay — the visible state
        // that was reported as never appearing. Saved to the test output
        // directory, never the repo tree.
        var bitmap = window.CaptureRenderedFrame();
        Assert.NotNull(bitmap);
        var pngPath = Path.Combine(AppContext.BaseDirectory, "MainWindow-A10Fix-TypeAheadOpen.png");
        using (var stream = new FileStream(pngPath, FileMode.Create, FileAccess.Write))
        {
            bitmap!.Save(stream, PngBitmapEncoderOptions.Default);
        }
        Console.WriteLine($"Screenshot: {pngPath}");
        ScreenshotAssertions.AssertDimensions(pngPath, 1024, 768);
        ScreenshotAssertions.AssertNotUniformColor(pngPath);

        // Now drive a selection through the real control, the way a user
        // would: type a prefix so the real AsyncPopulator path is exercised...
        autoCompleteBox.Text = "Black";
        Dispatcher.UIThread.RunJobs();

        // ...then set SelectedItem the way choosing a dropdown entry does
        // (AutoCompleteBox sets SelectedItem internally on a pick, which is
        // what raises SelectionChanged — the event OnTypeAheadSelectionChanged
        // handles). The item is built directly rather than by blocking on the
        // populator's Task from this thread: the populator awaits
        // Task.Yield(), and blocking the UI thread's own dispatcher for its
        // continuation would deadlock.
        var lotus = new CatalogItem("oracle-lotus", "Black Lotus");
        autoCompleteBox.SelectedItem = lotus;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(TileState.ManuallySet, tileVm.State);
        Assert.Equal("Black Lotus", tileVm.DisplayName);
        Assert.False(tileVm.IsTypeAheadOpen, "Overlay must close after a selection is made.");

        window.Close();
    }

    /// <summary>
    /// A10 known bug 1 (deferred at A10-fix, fixed here): "the type-ahead
    /// does not take keyboard focus in the LIVE window (a headless test
    /// previously passed while the live exe failed)." The test above closes
    /// the popup with an explicit <c>contextMenu.Close()</c> call and only
    /// checks focus afterwards — but never actually contests focus in
    /// between, so it could not see a race between "our own Focus() call"
    /// and "whatever the closing popup does to focus," which is exactly what
    /// happens on a REAL right-click (Avalonia's own menu-item-selection
    /// handling closes the popup and restores focus to whatever had it
    /// before, right after <c>Click</c> returns).
    /// </summary>
    /// <remarks>
    /// This test manufactures that race directly: it raises <c>Click</c>,
    /// then — BEFORE running any dispatcher jobs — moves focus away to
    /// simulate the popup's own close-time restoration stealing it back,
    /// THEN closes the menu and drains the dispatcher. Under the old
    /// synchronous <c>Focus()</c> call (fired inline with the
    /// <c>PropertyChanged</c> notification, itself inline with <c>Click</c>),
    /// the synthetic steal-back below runs strictly AFTER that call and
    /// wins, leaving focus on the tile — reproducing the live bug headlessly.
    /// The fix posts the real focus call via
    /// <c>Dispatcher.UIThread.Post(..., DispatcherPriority.Input)</c>, so it
    /// is still only QUEUED at the point the steal-back runs, and wins once
    /// <c>RunJobs()</c> drains the queue — after the steal-back, not before it.
    /// </remarks>
    [AvaloniaFact]
    public void RightClick_SetCardManually_FocusSurvivesContextMenuCloseStealingFocusBack()
    {
        var catalog = new InlineOracleCatalog([new OracleEntry("oracle-lotus", "Black Lotus")]);
        var session = MakeSession(catalog);
        var window = new MainWindow(session);
        window.Width = 1024;
        window.Height = 768;

        var vm = (MainViewModel)window.DataContext!;
        vm.LoadCohort(MakeSingleCohort());

        window.Show();
        Dispatcher.UIThread.RunJobs();

        var tileGrid = window.GetVisualDescendants()
            .OfType<Grid>()
            .FirstOrDefault(g => g.Name == "CohortTile");
        Assert.NotNull(tileGrid);

        var contextMenu = tileGrid!.ContextMenu;
        Assert.NotNull(contextMenu);

        var center = tileGrid.TranslatePoint(
            new Point(tileGrid.Bounds.Width / 2, tileGrid.Bounds.Height / 2), window);
        Assert.NotNull(center);

        window.MouseDown(center!.Value, MouseButton.Right);
        window.MouseUp(center!.Value, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
        Assert.True(contextMenu!.IsOpen);

        var menuItem = contextMenu.Items
            .OfType<MenuItem>()
            .FirstOrDefault(mi => mi.Header?.ToString() == "Set card manually…");
        Assert.NotNull(menuItem);

        // Raise Click — this is where the fixed code POSTS the focus call
        // rather than running it inline.
        menuItem!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));

        // Simulate the popup's own close-time focus restoration winning the
        // race: move focus away BEFORE any dispatcher job runs, then close
        // the menu — exactly the order a real right-click produces (Click,
        // then the native close-and-restore). The steal-back target is an
        // arbitrary RadioButton from the layout selector rather than the
        // tile Grid or the Window itself — both were tried and confirmed
        // (empirically, via a debug trace) to be no-ops on
        // GetFocusedElement() here, since neither is a real keyboard-focus
        // target; a RadioButton always is.
        var stealBackTarget = window.GetVisualDescendants().OfType<RadioButton>().First();
        stealBackTarget.Focus();
        contextMenu.Close();

        // Now drain the queue — this is where the deferred Focus() call
        // actually executes, and it must win because it was posted, not run
        // inline.
        Dispatcher.UIThread.RunJobs();

        var autoCompleteBox = window.GetVisualDescendants()
            .OfType<AutoCompleteBox>()
            .FirstOrDefault(a => a.DataContext == vm.Tiles[0]);
        Assert.NotNull(autoCompleteBox);
        Assert.True(autoCompleteBox!.IsVisible, "The type-ahead AutoCompleteBox must be visible.");

        var focused = window.FocusManager?.GetFocusedElement() as Visual;
        var focusedInBox = focused is not null &&
            (ReferenceEquals(focused, autoCompleteBox) ||
             focused.GetVisualAncestors().Contains(autoCompleteBox));
        Assert.True(focusedInBox,
            $"Focus must land in the type-ahead box even after the context menu's own close " +
            $"steals it back first; actual focus: {focused?.GetType().Name ?? "null"}");

        window.Close();
    }

    // -----------------------------------------------------------------------
    // Issue #18: "Set card manually…" didn't work by typing — these tests
    // drive REAL text input (window.KeyTextInput), REAL keyboard navigation
    // (window.KeyPressQwerty) and REAL pointer clicks through the box's own
    // TextBox, never TileViewModel or AutoCompleteBox.Text/SelectedItem set
    // directly — exactly the gap that let this regress twice while headless
    // tests passed (see the type-level remarks above and CLAUDE.md's issue
    // description). Chaos-test results are at the bottom of this file.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Typing a lowercase, mixed-case prefix ("bag o") must find "Bag of
    /// Holding" (stored title-cased) in what the dropdown actually DISPLAYS,
    /// and picking it with the keyboard must set the tile — all without
    /// Enter ever reaching <see cref="MainWindow.OnEnterAsync"/> and
    /// committing the whole pending cohort.
    /// </summary>
    [AvaloniaFact]
    public async Task TypeAhead_TypingCaseInsensitivePrefix_FindsCardInDropdown_AndKeyboardPickSetsManuallySet()
    {
        var catalog = new InlineOracleCatalog([
            new OracleEntry("oracle-boh", "Bag of Holding"),
            new OracleEntry("oracle-decoy1", "Decoy Card One"),
            new OracleEntry("oracle-decoy2", "Decoy Card Two"),
        ]);
        var session = MakeSession(catalog);
        var window = new MainWindow(session);
        window.Width = 1024;
        window.Height = 768;

        var vm = (MainViewModel)window.DataContext!;
        vm.LoadCohort(MakeSingleCohort());
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var tileGrid = FindTileGrid(window);
        var tileVm = (TileViewModel)tileGrid.DataContext!;
        var acb = OpenTypeAhead(window, tileGrid);

        // Real text input, lowercase and mid-word-cased against the stored
        // "Bag of Holding" — the exact case-sensitivity gap this issue fixes.
        window.KeyTextInput("bag o");
        await WaitUntilAsync(() => acb.IsDropDownOpen, TimeSpan.FromSeconds(5));

        // Assert against what the dropdown actually DISPLAYS — the
        // control's own FilterMode is one of the three fixed causes, so the
        // populator returning the right thing is not enough on its own.
        var displayed = DisplayedDropdownNames(acb);
        Assert.Contains("Bag of Holding", displayed);

        window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(TileState.ManuallySet, tileVm.State);
        Assert.Equal("Bag of Holding", tileVm.DisplayName);
        Assert.False(tileVm.IsExcluded);

        // Enter went to the focused box, so the window's tunnel handler bailed
        // and OnEnterAsync never ran — the cohort is still loaded.
        Assert.Single(vm.Tiles);

        window.Close();
    }

    /// <summary>
    /// Arrow keys move the dropdown's highlight; they must not pick. Found
    /// live: the first ArrowDown set the tile to the top entry and closed the
    /// list, so only the first match was ever reachable by keyboard. Enter
    /// picks the highlighted entry.
    /// </summary>
    [AvaloniaFact]
    public async Task TypeAhead_ArrowKeysOnlyHighlight_EnterPicksTheHighlightedEntry()
    {
        var catalog = new InlineOracleCatalog([
            new OracleEntry("oracle-boh", "Bag of Holding"),
            new OracleEntry("oracle-bot", "Bag of Tricks"),
            new OracleEntry("oracle-bod", "Bag of Devouring"),
        ]);
        var session = MakeSession(catalog);
        var window = new MainWindow(session);
        window.Width = 1024;
        window.Height = 768;

        var vm = (MainViewModel)window.DataContext!;
        vm.LoadCohort(MakeSingleCohort());
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var tileGrid = FindTileGrid(window);
        var tileVm = (TileViewModel)tileGrid.DataContext!;
        var acb = OpenTypeAhead(window, tileGrid);

        window.KeyTextInput("bag o");
        await WaitUntilAsync(() => acb.IsDropDownOpen, TimeSpan.FromSeconds(5));
        var third = DisplayedDropdownNames(acb)[2];

        window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.False(tileVm.IsManuallySet, "Arrow keys must only move the highlight, not pick an entry.");
        Assert.True(tileVm.IsTypeAheadOpen, "The overlay must stay open while arrowing through the list.");
        Assert.True(acb.IsDropDownOpen, "The dropdown must stay open while arrowing through the list.");

        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(TileState.ManuallySet, tileVm.State);
        Assert.Equal(third, tileVm.DisplayName);
        Assert.False(tileVm.IsTypeAheadOpen, "Overlay must close after Enter picks an entry.");
        Assert.Single(vm.Tiles); // Enter went to the box, not to the cohort commit

        window.Close();
    }

    /// <summary>
    /// Escape abandons the dropdown without picking, even with an entry
    /// highlighted.
    /// </summary>
    [AvaloniaFact]
    public async Task TypeAhead_EscapeWithAnEntryHighlighted_LeavesTheTileUnchanged()
    {
        var catalog = new InlineOracleCatalog([
            new OracleEntry("oracle-boh", "Bag of Holding"),
            new OracleEntry("oracle-bot", "Bag of Tricks"),
        ]);
        var session = MakeSession(catalog);
        var window = new MainWindow(session);
        window.Width = 1024;
        window.Height = 768;

        var vm = (MainViewModel)window.DataContext!;
        vm.LoadCohort(MakeSingleCohort());
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var tileGrid = FindTileGrid(window);
        var tileVm = (TileViewModel)tileGrid.DataContext!;
        var acb = OpenTypeAhead(window, tileGrid);

        window.KeyTextInput("bag o");
        await WaitUntilAsync(() => acb.IsDropDownOpen, TimeSpan.FromSeconds(5));

        window.KeyPressQwerty(PhysicalKey.ArrowDown, RawInputModifiers.None);
        window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.False(tileVm.IsManuallySet, "Escape must not pick the highlighted entry.");
        Assert.Equal("Lightning Bolt", tileVm.DisplayName);
        Assert.Single(vm.Tiles); // Escape in the box must not clear the cohort

        window.Close();
    }

    /// <summary>
    /// Typing text that matches the MIDDLE of one name and the START of
    /// another must surface both, with the prefix match ranked first — the
    /// two-tier ranking <see cref="TileViewModel"/>'s populator now applies.
    /// </summary>
    [AvaloniaFact]
    public async Task TypeAhead_TypingSubstring_FindsBothMatches_PrefixRankedBeforeSubstring()
    {
        var catalog = new InlineOracleCatalog([
            new OracleEntry("oracle-hc", "Holding Cell"),    // starts with "Holding"
            new OracleEntry("oracle-boh", "Bag of Holding"), // contains "Holding" only in the middle
        ]);
        var session = MakeSession(catalog);
        var window = new MainWindow(session);
        window.Width = 1024;
        window.Height = 768;

        var vm = (MainViewModel)window.DataContext!;
        vm.LoadCohort(MakeSingleCohort());
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var tileGrid = FindTileGrid(window);
        var acb = OpenTypeAhead(window, tileGrid);

        window.KeyTextInput("holding");
        await WaitUntilAsync(() => acb.IsDropDownOpen, TimeSpan.FromSeconds(5));

        var displayed = DisplayedDropdownNames(acb);
        Assert.Contains("Holding Cell", displayed);
        Assert.Contains("Bag of Holding", displayed);
        Assert.True(
            displayed.IndexOf("Holding Cell") < displayed.IndexOf("Bag of Holding"),
            $"Expected the prefix match ('Holding Cell') before the substring-elsewhere match " +
            $"('Bag of Holding'); displayed order was: {string.Join(", ", displayed)}");

        window.Close();
    }

    /// <summary>
    /// A real pointer click inside the box's own <c>TextBox</c> part (e.g.
    /// positioning the caret before typing) must not toggle the tile's X. A
    /// pointer pick of a dropdown item must set the tile and must also not
    /// toggle the X.
    /// </summary>
    /// <remarks>
    /// The TextBox click is sent before any text is typed. Once the dropdown
    /// is open, Avalonia's light-dismiss overlay covers the window and a
    /// click at the TextBox's coordinates never reaches it. The dropdown item
    /// can't be hit-tested headlessly (the popup has no top level), so
    /// <see cref="ClickDropdownItem"/> raises the press and release on the
    /// item directly; the box's own selection adapter still handles them.
    /// </remarks>
    [AvaloniaFact]
    public async Task TypeAhead_PointerClickInTextBox_DoesNotToggleExcluded_AndSelectingItemSetsManuallySet()
    {
        var catalog = new InlineOracleCatalog([new OracleEntry("oracle-boh", "Bag of Holding")]);
        var session = MakeSession(catalog);
        var window = new MainWindow(session);
        window.Width = 1024;
        window.Height = 768;

        var vm = (MainViewModel)window.DataContext!;
        vm.LoadCohort(MakeSingleCohort());
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var tileGrid = FindTileGrid(window);
        var tileVm = (TileViewModel)tileGrid.DataContext!;
        var acb = OpenTypeAhead(window, tileGrid);
        Assert.False(acb.IsDropDownOpen, "Precondition: dropdown must still be closed for the click below to be hit-testable.");

        // Real pointer click inside the box's own TextBox part, before
        // typing. This bubbles Tapped up through the AutoCompleteBox to the
        // tile Grid (both are fully inside the window's own visual tree).
        var innerTextBox = acb.GetVisualDescendants().OfType<TextBox>().First();
        var tbCenter = innerTextBox.TranslatePoint(
            new Point(innerTextBox.Bounds.Width / 2, innerTextBox.Bounds.Height / 2), window);
        Assert.NotNull(tbCenter);
        window.MouseDown(tbCenter!.Value, MouseButton.Left);
        window.MouseUp(tbCenter!.Value, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.False(tileVm.IsExcluded, "Clicking inside the type-ahead TextBox must not toggle the X.");

        // Now type to open the dropdown, then pick an item.
        window.KeyTextInput("bag o");
        await WaitUntilAsync(() => acb.IsDropDownOpen, TimeSpan.FromSeconds(5));

        var popup = acb.GetVisualDescendants().OfType<Popup>().First();
        var item = popup.Child!.GetVisualDescendants().OfType<ListBoxItem>().First();
        ClickDropdownItem(item);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(TileState.ManuallySet, tileVm.State);
        Assert.Equal("Bag of Holding", tileVm.DisplayName);
        Assert.False(tileVm.IsExcluded, "Selecting a dropdown item must not toggle the X.");

        window.Close();
    }

    // -----------------------------------------------------------------------
    // Shared helpers for the typing/pointer tests above
    // -----------------------------------------------------------------------

    /// <summary>Finds the single cohort tile's outer Grid by its x:Name.</summary>
    private static Grid FindTileGrid(MainWindow window)
    {
        var tileGrid = window.GetVisualDescendants().OfType<Grid>().FirstOrDefault(g => g.Name == "CohortTile");
        Assert.NotNull(tileGrid);
        return tileGrid!;
    }

    /// <summary>
    /// Right-clicks <paramref name="tileGrid"/> to open its real
    /// <c>ContextMenu</c>, raises the real "Set card manually…"
    /// <c>MenuItem.Click</c>, closes the menu and returns the tile's
    /// <c>AutoCompleteBox</c> — the same opening sequence the tests above use.
    /// </summary>
    private static AutoCompleteBox OpenTypeAhead(MainWindow window, Grid tileGrid)
    {
        var contextMenu = tileGrid.ContextMenu;
        Assert.NotNull(contextMenu);

        var center = tileGrid.TranslatePoint(
            new Point(tileGrid.Bounds.Width / 2, tileGrid.Bounds.Height / 2), window);
        Assert.NotNull(center);

        window.MouseDown(center!.Value, MouseButton.Right);
        window.MouseUp(center!.Value, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();

        var menuItem = contextMenu!.Items
            .OfType<MenuItem>()
            .FirstOrDefault(mi => mi.Header?.ToString() == "Set card manually…");
        Assert.NotNull(menuItem);

        menuItem!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        contextMenu.Close();
        Dispatcher.UIThread.RunJobs();

        var tileVm = (TileViewModel)tileGrid.DataContext!;
        var acb = window.GetVisualDescendants().OfType<AutoCompleteBox>().FirstOrDefault(a => a.DataContext == tileVm);
        Assert.NotNull(acb);
        return acb!;
    }

    /// <summary>
    /// Clicks a dropdown entry. The headless popup has no coordinate space to
    /// click into, so this raises the pointer press and release a real click
    /// produces on the item itself, which is what the box's selection adapter
    /// listens to.
    /// </summary>
    private static void ClickDropdownItem(ListBoxItem item)
    {
        var pointer = new Pointer(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
        var pressed = new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed);
        var released = new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased);

        item.RaiseEvent(new PointerPressedEventArgs(item, pointer, item, default, 0, pressed, KeyModifiers.None));
        item.RaiseEvent(new PointerReleasedEventArgs(item, pointer, item, default, 0, released, KeyModifiers.None, MouseButton.Left));
    }

    /// <summary>
    /// The names the dropdown's own popup actually shows, in display order —
    /// found by walking the realised <c>Popup.Child</c>'s visual tree for
    /// <c>TextBlock</c>s, never the populator's return value. The populator
    /// is only half the fix (cause 1); the control's own <c>FilterMode</c>
    /// (cause 2) can still drop or reorder what it returns.
    /// </summary>
    private static List<string?> DisplayedDropdownNames(AutoCompleteBox acb)
    {
        var popup = acb.GetVisualDescendants().OfType<Popup>().FirstOrDefault();
        Assert.NotNull(popup);
        Assert.NotNull(popup!.Child);
        return popup.Child!.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
    }

    /// <summary>
    /// Pumps the dispatcher until <paramref name="condition"/> holds or
    /// <paramref name="timeout"/> elapses. The populator now runs via
    /// <c>Task.Run</c> and the box's 150 ms populate delay is a real timer,
    /// so a single <c>RunJobs()</c> right after typing is not enough — see
    /// <c>A10CohortScreenshotTests.WaitUntilAsync</c> for the same pattern.
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            Dispatcher.UIThread.RunJobs();
            if (condition())
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition not met within timeout.");
            }

            await Task.Delay(10);
        }
    }

    // -----------------------------------------------------------------------
    // Fakes / helpers
    // -----------------------------------------------------------------------

    private sealed class NullScanPipeline : IScanPipeline
    {
#pragma warning disable CS0067
        public event Action<CameraFrame, DetectionSnapshot>? FrameProcessed;
        public event Action<Cohort>? AutoCaptured;
        public event Action<FrameSourceException>? SourceFailed;
#pragma warning restore CS0067
        public string SourceDescription => "headless-test";
        public Task<Cohort?> CaptureAsync(CancellationToken ct) => Task.FromResult<Cohort?>(null);
        public Task RunAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NullFrameSource : IFrameSource
    {
        public string Description => "headless-test";
        public FrameGeometry Geometry => new(1, 1, 0);
        public IAsyncEnumerable<CameraFrame> ReadAsync(CancellationToken ct) => EmptyAsync();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        private static async IAsyncEnumerable<CameraFrame> EmptyAsync()
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class InlineOracleCatalog : IOracleCatalog
    {
        public InlineOracleCatalog(IReadOnlyList<OracleEntry> entries) => All = entries;
        public IReadOnlyList<OracleEntry> All { get; }
    }
}

/*
 * Chaos-test results (see docs/TESTING.md "Standing practice: chaos-test every regression test"),
 * A10-bugs session, 2026-09-22:
 *
 * RightClick_SetCardManually_FocusSurvivesContextMenuCloseStealingFocusBack
 * (the new regression test for A10 known bug 1): re-applied the original bug
 * by reverting OnTypeAheadPropertyChanged's Dispatcher.UIThread.Post(...,
 * DispatcherPriority.Input) wrapper back to a synchronous Focus() call, ran
 * ONLY this test, and it failed for the right reason:
 *   "Focus must land in the type-ahead box even after the context menu's own
 *    close steals it back first; actual focus: RadioButton"
 * — i.e. the synthetic steal-back (focusing an unrelated RadioButton right
 * after Click, before any dispatcher job runs) won, exactly reproducing the
 * live-window symptom headlessly. The fix was then reverted back to the
 * Dispatcher.Post version and the test passes again.
 *
 * First attempt at this chaos test was itself broken: it used
 * `tileGrid.Focus()` (a plain Grid, not focusable — a silent no-op) and then
 * `window.Focus()` (also confirmed empirically to not move
 * GetFocusedElement() away from a child control) as the "steal-back", so it
 * passed against the unfixed code too — a vacuous regression test. Switching
 * to an actually-focusable control (a RadioButton already in the visual
 * tree) made the steal-back real and the chaos test meaningful.
 *
 * The existing RightClick_SetCardManually_OpensFocusedTypeAhead_... test
 * (added at A10-fix) was re-run against the same reverted code and still
 * PASSED — confirming the orchestration plan's diagnosis that this test
 * cannot see the race: it closes the ContextMenu with no steal-back in
 * between, so there is nothing for the old synchronous Focus() call to lose
 * against.
 *
 * Issue #18 session, 2026-10-02 — chaos-testing the three typing-search causes:
 *
 * Cause (a) — TileViewModel.BuildPopulator reverted to ordinal,
 * case-sensitive, prefix-only matching (StartsWith(text, Ordinal), substring
 * branch forced to false):
 *   - Populator_CaseInsensitive_MatchesRegardlessOfTypedCase: FAILED —
 *     `Assert.Contains() Failure: Filter not matched in collection /
 *     Collection: []` ("bag o" no longer matches "Bag of Holding").
 *   - Populator_SubstringMatch_IsIncluded_RankedAfterPrefixMatch: FAILED —
 *     `Collection: [Bolt]` ("Lightning Bolt" dropped entirely).
 *   - TypeAhead_TypingCaseInsensitivePrefix_FindsCardInDropdown_...: FAILED —
 *     `System.TimeoutException: Condition not met within timeout` waiting for
 *     `acb.IsDropDownOpen` — with zero matches, the control never opens the
 *     dropdown at all, which is itself exactly the live symptom ("typing
 *     does nothing").
 *   - TypeAhead_TypingSubstring_FindsBothMatches_...: FAILED the same way
 *     (timeout — "holding" matches nothing case/prefix-exact).
 *   Reverted; all four pass again.
 *
 * Cause (b) — MainWindow.OnTypeAheadLoaded's `acb.FilterMode =
 * AutoCompleteFilterMode.None;` line commented out (left at the control's
 * default, StartsWith):
 *   - TypeAhead_TypingSubstring_FindsBothMatches_PrefixRankedBeforeSubstring:
 *     FAILED — `Assert.Contains() Failure: Item not found in collection /
 *     Collection: ["Holding Cell"] / Not found: "Bag of Holding"` — the
 *     control's own default filter re-applied StartsWith to the populator's
 *     already-correct two-tier result and silently dropped the
 *     substring-elsewhere match.
 *   - TypeAhead_TypingCaseInsensitivePrefix_...: still PASSED — "bag o" is a
 *     true (case-insensitive) prefix of "Bag of Holding", and the default
 *     FilterMode's own comparison is already case-insensitive, so this test
 *     alone cannot see cause (b); that is exactly why the substring test
 *     above exists as a separate case.
 *   Reverted; the substring test passes again.
 *
 * Cause (c) — MainWindow.OnTileTapped's logical-ancestor guard commented out:
 *   - TypeAhead_PointerClickInTextBox_DoesNotToggleExcluded_...: FAILED —
 *     "Clicking inside the type-ahead TextBox must not toggle the X." A
 *     diagnostic spike with handlers attached directly to the inner TextBox
 *     confirmed the mechanism precisely: `tileGrid.Tapped` fires with
 *     `source=TextPresenter` (a part inside the TextBox), and with the guard
 *     removed `tileVm.IsExcluded` becomes `true`; with the guard restored,
 *     the same Tapped still reaches `tileGrid.Tapped` (so the test is not
 *     vacuous — it is exercising real bubbling) but `IsExcluded` stays
 *     `false`.
 *   - The dropdown-item half of the guard could not be chaos-tested: under
 *     headless, the popup's content is not visually attached to the window,
 *     so a Tapped raised on an item never reaches the tile Grid with or
 *     without the guard. That half is untested, not vacuously tested.
 *   Reverted; the test passes again.
 *
 * Cause (d), found in the live app after the above — ArrowDown picked the
 * first entry and closed the list. Re-planted by removing the
 * `{ IsDropDownOpen: false }` condition in OnTypeAheadSelectionChanged, so
 * every highlight change picks again:
 *   - TypeAhead_ArrowKeysOnlyHighlight_EnterPicksTheHighlightedEntry: FAILED —
 *     "Arrow keys must only move the highlight, not pick an entry."
 *   - TypeAhead_EscapeWithAnEntryHighlighted_LeavesTheTileUnchanged: FAILED —
 *     "Escape must not pick the highlighted entry."
 *   - TypeAhead_TypingCaseInsensitivePrefix_...: FAILED too (it now picks
 *     with ArrowDown + Enter).
 *   Both new tests also failed against the code before the fix, on the same
 *   assertions. Restored; all pass again.
 */
