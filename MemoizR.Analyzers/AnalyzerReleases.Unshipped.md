; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
MZR001  | Concurrency | Error | Value type shared by the reactive graph is not Sendable
MZR002  | Concurrency | Error | Reactive computation mutates state shared with code outside it
MZR003  | Concurrency | Error | Signal.Set inside a reactive computation throws at runtime
MZR004  | Concurrency | Warning | Static state shared with the reactive graph is not data-race safe
MZR005  | Concurrency | Warning | Value used after being transferred
MZR006  | Concurrency | Info | Non-sealed class shared by the reactive graph can smuggle mutable subclass state
