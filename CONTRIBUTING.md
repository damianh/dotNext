Contribution to .NEXT
====

This project welcomes contributions and suggestions.  Most contributions require you to agree to a
Contributor License Agreement (CLA) declaring that you have the right to, and actually do, grant us
the rights to use your contribution. For details, visit https://cla.microsoft.com.

When you submit a pull request, a CLA-bot will automatically determine whether you need to provide
a CLA and decorate the PR appropriately (e.g., label, comment). Simply follow the instructions
provided by the bot. You will only need to do this once across all repos using our CLA.

This project has adopted the [.NET Foundation Code of Conduct](https://dotnetfoundation.org/code-of-conduct).
For more information see the [Code of Conduct FAQ](https://www.contributor-covenant.org/faq/) or
contact [conduct@dotnetfoundation.org](mailto:conduct@dotnetfoundation.org) with any additional questions or comments.

## Branching Model
This repository uses branching model known as [git flow](https://nvie.com/posts/a-successful-git-branching-model/). Use **develop** as the destination branch in your Pull Request.

Since 5.x release, squash commit is used to merge all commits related to the release when moving to `main` branch.

## Backward Compatibility
Contributions must not contain breaking changes such as backward incompatible modification of API signatures. The only exception is a new major version of the library. However, it should pass through code review and discussion.

## Unit Tests
If your PR contains bug fix or new feature then it should have unit tests.

## Continuous Integration

The [CI workflow](https://github.com/damianh/dotNext/actions/workflows/ci.yml) runs on all pull requests, pushes to `fork`, `master`, and `develop`, and manual dispatch. Both jobs use Ubuntu x64 and the .NET SDK selected by `global.json`:

- **Build, tests, and coverage** builds the full solution, including examples and benchmarks, then runs the managed tests in Debug with a 10-minute test timeout. Debug is required because tests access library internals exposed only in this configuration. Benchmarks are built but not executed.
- **Native AOT tests** publishes the AOT test project in Release for `linux-x64` and executes the native binary, independently of the managed tests.

### Coverage and test results

Open a workflow run's **Summary** to see the managed code coverage table. Under **Artifacts**, download `coverage-report`, extract the archive, and open `index.html` for per-assembly and source-level coverage. The report also includes `SummaryGithub.md`, which supplies the run summary. Coverage covers product assemblies, excluding test assemblies and generated `.g.cs` files; it does not include the Native AOT run.

The `managed-test-results` artifact contains TRX test results and raw `coverage.cobertura.xml`; `native-aot-test-results` contains the native tests' TRX results. Artifacts are retained for 14 days. Available reports are uploaded even if tests fail, and test failures still fail CI. There is no coverage threshold, external coverage service, or PR comment requiring write permissions.

### Running locally

From the repository root, use the .NET 10 Microsoft.Testing.Platform runner configured in `global.json`:

```powershell
dotnet restore .\src\DotNext.slnx --configfile .\NuGet.config
dotnet build .\src\DotNext.slnx --configuration Debug --no-restore
dotnet test --project .\src\DotNext.Tests\DotNext.Tests.csproj --configuration Debug --no-build --timeout 10m --results-directory "$PWD\TestResults\managed" --report-trx --report-trx-filename tests.trx --coverage --coverage-output-format cobertura --coverage-output "$PWD\TestResults\managed\coverage.cobertura.xml"
```

On Linux, use `/` path separators. To run Native AOT tests on Linux x64 with `clang` and the zlib development headers installed:

```bash
dotnet publish src/DotNext.Aot.Tests/DotNext.Aot.Tests.csproj --configuration Release --runtime linux-x64 --configfile NuGet.config --output TestResults/aot-bin
./TestResults/aot-bin/DotNext.Aot.Tests --results-directory "$PWD/TestResults/aot" --report-trx --report-trx-filename tests.trx
```

The existing Azure pipeline, package signing/publishing, and CodeQL workflow are unchanged.

## AI
If your PR was fully or partially made by an AI model (Codex, Claude, etc.), add the `ai_assisted` label to it.
