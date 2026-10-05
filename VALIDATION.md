# Retention update validation

Validated 30 September 2026 (South African time).

Passed against an isolated MySQL 8.4.6 database using the actual updated EF models, creation controllers and retention maintenance code:
- Upgrade from tables without deletion columns; repeat upgrade safely.
- Default checked request values for both builders.
- Quiz and survey creation persist a 24-calendar-month deadline when checked and NULL when unchecked.
- Due quiz and survey are physically removed.
- Old unchecked records and future deadlines survive cleanup.
- Dependent quiz attempts, legacy ledger rows, survey completions and responses are removed.
- Exclusive questions and options are removed; questions shared with a surviving quiz remain.
- Legacy active survey selection is cleared.
- Repeated cleanup succeeds without deleting retained items.

Compilation:
- API/shared source compiled using the available dependency DLLs. Ten existing nullable DbSet initialization warnings were reported by this isolated compilation.
- All client C# and Razor pages compiled using the Razor SDK and available dependency DLLs with zero errors or warnings.
- The unused template WeatherForecastController referenced a missing WeatherForecast class in the supplied source and was removed to avoid a compilation error.

Limitations:
- Normal NuGet restore/full solution build was unavailable in this environment because the package cache was incomplete. Isolated compilation used the dependencies from the previously built project.
- Full WebAssembly bundling and browser end-to-end testing were not performed here. Rebuild and run the full solution locally.
- Tests invoked the creation controllers and cleanup code directly against MySQL, not through browser authentication or HTTP.
