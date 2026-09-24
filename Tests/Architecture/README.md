# LoreFetch.Tests.Architecture

Dependency rules between the domains, enforced with [ArchUnitNET](https://github.com/TNG/ArchUnitNET) against the compiled `App`, `Capture`, `Core` and `Lab` assemblies. Each rule enforces a direction the docs already state, and cites where. The list and the reasoning are in [`../../docs/CONTRACTS.md`](../../docs/CONTRACTS.md#dependency-rules).

A failure names the rule, its reason, and every offending type. Either the change is wrong, or the design decision is changing. In the second case, update the rule **and** the doc it cites in the same PR, and say why.

## Running

```sh
scripts/lorefetch.sh test
# or directly:
dotnet test Tests/Architecture/LoreFetch.Tests.Architecture.csproj -c Release
```

## Adding a rule

Add a `[Fact]` to `DependencyRuleTests.cs` that cites the doc it enforces, then **chaos-test it**: plant a class that breaks it, confirm the rule fails naming that class, and delete the plant. A rule whose type filter matches nothing passes forever. Each existing rule was checked that way when it was added.

ArchUnitNET reads IL, and its authors recommend Debug builds, because optimisation can in principle remove a dependency. That can only hide a violation, never invent one, and the chaos tests above were run in Release, which is what CI runs.
