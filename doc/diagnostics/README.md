# Sharp.Mediator diagnostics

Each diagnostic ID has its own page with the message, the cause, how to fix it, and how to suppress it.

| ID | Severity | Title |
| --- | --- | --- |
| [`SMD001`](SMD001.md) | Error | Request type implements multiple response types |
| [`SMD002`](SMD002.md) | Error | Request type implements both IRequest and IRequest<TResponse> |
| [`SMD003`](SMD003.md) | Warning | Message type is not accessible from generated code |
| [`SMD004`](SMD004.md) | Error | Stream request type implements multiple response types |
| [`SMD005`](SMD005.md) | Error | Request or stream has multiple handlers |
| [`SMD006`](SMD006.md) | Warning | Generic handler is not discovered |
| [`SMD007`](SMD007.md) | Warning | Handler is not accessible from generated code |
| [`SMD008`](SMD008.md) | Error | Request or stream handler is declared for an interface |
| [`SMD010`](SMD010.md) | Warning | Open-generic middleware cannot be closed at build time |
| [`SMD011`](SMD011.md) | Warning | Open-generic middleware shape is not supported |
| [`SMD012`](SMD012.md) | Warning | Closed middleware can never run |
| [`SMD020`](SMD020.md) | Error | Language version is too old for the generated code |
