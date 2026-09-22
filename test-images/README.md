# test-images/

Local card imagery for development and accuracy runs. **Everything in this folder except this README is gitignored** and must never be committed — card artwork is Wizards of the Coast IP regardless of who photographed it (see `CLAUDE.md`).

The folder sits at the same relative path in every checkout, on both the Mac and the Windows PC, so tests and `LoreFetch.Lab` can find it at `<repo>/test-images/` without per-machine configuration. Keep the two machines in sync by hand, and back the folder up outside git.

Layout:

```
test-images/
  ad-hoc/                     loose images for quick manual checks
  fixtures/<height>in/<layout>/*.png   the H3 fixture corpus (heights 12/20, layouts 1/3/9; the 9 layout is
                                        laid out with its cards rotated, long edge across the frame)
  ground-truth.csv            file,height_in,layout,slot,oracle_name,rung,mat
```

Fixtures are `.png`, not `.jpg`: the Stream C hardware harness saves a lossless PNG of the decoded BGR frame, and the camera's own MJPG artifacts are already baked into those pixels, so re-encoding to JPEG would stack a second lossy generation on top for nothing.

Tests that need these files **skip with a reason** when they are absent, so CI stays green; see `docs/orchestration-plan.md` (V7).

Check that a file really is ignored with `git check-ignore -v test-images/<path>`.
