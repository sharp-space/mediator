; Shipped analyzer releases
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

## Release 1.0.0

### New Rules

Rule ID  | Category       | Severity | Notes
---------|----------------|----------|--------------------------------------------------------------------
SMD001 | Sharp.Mediator | Error    | Request type implements multiple response types
SMD002 | Sharp.Mediator | Error    | Request type implements both IRequest and IRequest\<TResponse\>
SMD003 | Sharp.Mediator | Warning  | Message type is not accessible from generated code
SMD004 | Sharp.Mediator | Error    | Stream request type implements multiple response types
SMD005 | Sharp.Mediator | Error    | Request or stream has multiple handlers
SMD006 | Sharp.Mediator | Warning  | Generic handler is not discovered
SMD007 | Sharp.Mediator | Warning  | Handler is not accessible from generated code
SMD008 | Sharp.Mediator | Error    | Request or stream handler is declared for an interface
SMD010 | Sharp.Mediator | Warning  | Open-generic middleware cannot be closed at build time
SMD011 | Sharp.Mediator | Warning  | Open-generic middleware shape is not supported
SMD012 | Sharp.Mediator | Warning  | Closed middleware can never run
SMD020 | Sharp.Mediator | Error    | Language version is too old for the generated code
