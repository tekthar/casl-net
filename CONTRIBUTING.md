# Contributing

Thanks for helping. Issues and pull requests are welcome.

## Before you open a pull request

1. Run `dotnet test`. It must pass on .NET 8 and .NET 10 with warnings treated as errors.
2. Run the CASL compatibility test: `cd tests/casl-compat && npm install && npm test`.
3. If you change semantics or add an operator, add cases to `tests/Casl.Net.Tests/RandomRules.cs` so the equivalence and CASL tests cover it.
4. Sign off your commits (`git commit -s`). By signing off you certify the [Developer Certificate of Origin](https://developercertificate.org/): you wrote the change or have the right to submit it under the project's licence.

## Design rules

- **Fail closed.** A rule that can't be enforced exactly must fail the build of the ability, never be skipped.
- **One meaning everywhere.** Anything `Filter<T>` translates to SQL must give the same answer in memory and in `@casl/ability`. The tests are the definition.
- **No dependencies** in `CASL .NET` beyond the .NET base class library.

## Releasing

Maintainers release from `main`:

1. Set the version in `Directory.Build.props` (`VersionPrefix`) and, when the browser package changed, in
   `packages/casl-net` (`npm version <x.y.z> --no-git-tag-version`).
2. Add the release to `CHANGELOG.md`.
3. Commit, then tag and push: `git tag v<x.y.z> && git push origin v<x.y.z>`.

The `release` workflow builds and tests on .NET 8 and .NET 10, runs the CASL compatibility test, checks that the tag
matches the version, publishes `Casl.Net` to NuGet and `casl-net` to npm through trusted publishing (no stored keys),
and creates a GitHub release with the packages attached. A version already on a registry is skipped, so a failed
release can be re-run.

## Code of conduct

Be respectful and assume good intent. Harassment or discrimination of any kind isn't tolerated. Report problems to the maintainers privately.
