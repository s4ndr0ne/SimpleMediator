; Shipped analyzer releases
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

## Release 4.1.0

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
SMG000 | SimpleMediator.Generator | Warning | Internal source generator failure
SMG001 | SimpleMediator.Generator | Warning | Multiple request handlers for the same request
SMG002 | SimpleMediator.Generator | Warning | Handler or behavior not accessible from generated code
SMG003 | SimpleMediator.Generator | Warning | Send/Publish argument is an open generic type
SMG004 | SimpleMediator.Generator | Info | Open-generic handler or behavior matched no request
SMG005 | SimpleMediator.Generator | Warning | AddBehavior argument is not a typeof expression
SMG006 | SimpleMediator.Generator | Warning | RegisterAssembly argument cannot be resolved at compile time
