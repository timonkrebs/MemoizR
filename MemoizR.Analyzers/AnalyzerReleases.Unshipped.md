; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
MZR001  | Concurrency | Warning | Value type shared by the reactive graph is not Sendable
MZR002  | Concurrency | Warning | Reactive computation mutates state shared with code outside it
MZR003  | Concurrency | Warning | Signal.Set inside a reactive computation throws at runtime
