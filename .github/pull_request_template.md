Fixes #

## What and why

<!-- What changed, and the reasoning. The reasoning is the part worth keeping. -->

## Checklist

- [ ] Bug fix: a regression test in the test project of the domain it touches
- [ ] Chaos-tested: re-applied the bug, saw the new test fail for the right reason, reverted
- [ ] Golden hashes untouched, or the index was rebuilt on win-x64 and the reason is stated above
- [ ] README or docs updated if behaviour changed
- [ ] No card imagery added (the pre-commit hook checks, but look anyway)

<!--
Labels that tell a failing check you meant it:
  contract-change   this PR edits the contract surface or project files
  tests-removed     this PR deletes tests on purpose (say why above)
  coverage-drop     this PR lowers coverage on purpose (say why above)
-->
