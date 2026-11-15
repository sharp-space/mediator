#!/usr/bin/env bash
# Installs the packed core package the way a consumer does and runs a two-assembly app, under the JIT and as a
# ReadyToRun publish, with warnings as errors, then a host that gets the package only through a project reference. In-repo projects cannot catch what this does: test/Directory.Build.props
# wires the generator in directly, and ReadyToRun initializes assemblies later than the JIT, which once lost
# middleware only in published builds.
set -euo pipefail

packages_dir="$(cd "${1:?usage: verify-consumer.sh <packages-dir>}" && pwd)"

matches=()
while IFS= read -r match; do
    matches+=("$match")
done < <(find "$packages_dir" -type f -name 'Sharp.Mediator.[0-9]*.nupkg' ! -name '*.snupkg')
if [ "${#matches[@]}" -ne 1 ]; then
    echo "FAIL: expected exactly one Sharp.Mediator package, found ${#matches[@]}" >&2
    exit 1
fi
version="$(basename "${matches[0]}" .nupkg)"
version="${version#Sharp.Mediator.}"

# Resolved to the physical path, because macOS puts the default under /var/folders, a symlink to /private/var/folders.
# MSBuild builds a project's path from $PWD, but restore resolves project references against the working directory
# the OS reports, which is physical. Under a symlinked $PWD, Host's reference to Module then names no project in the
# restore graph, NuGet drops it without a warning, and Host loses the package it gets only through Module.
work="$(cd "$(mktemp -d)" && pwd -P)"
trap 'rm -rf "$work"' EXIT
export NUGET_PACKAGES="$work/nuget"

cat > "$work/NuGet.config" <<XML
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$packages_dir" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
XML

mkdir -p "$work/Module" "$work/App" "$work/Host"

cat > "$work/Module/Module.csproj" <<XML
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Sharp.Mediator" Version="$version" />
  </ItemGroup>
</Project>
XML

cat > "$work/Module/Module.cs" <<'CS'
using Microsoft.Extensions.DependencyInjection;
using Sharp.Mediator;

namespace Module;

public sealed record ModulePing : IRequest<string?>;

public sealed class ModulePingHandler : IRequestHandler<ModulePing, string?>
{
    public ValueTask<string?> Handle(ModulePing message, CancellationToken cancellationToken) =>
        ValueTask.FromResult<string?>("module");
}

public static class ModuleRegistration
{
    public static IServiceCollection AddModule(this IServiceCollection services) =>
        services.AddMediator(x => x.AddHandlers(typeof(ModuleRegistration).Assembly));
}

public static class ModuleEndpoint
{
    public static async Task<string> Call(IServiceProvider services)
    {
        var reply = await services.GetRequiredService<IMediator>().Request(new ModulePing());
        return $"{reply}:{services.GetServices<IMiddleware<ModulePing, string?>>().Count()}";
    }
}
CS

cat > "$work/App/App.csproj" <<XML
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../Module/Module.csproj" />
    <PackageReference Include="Sharp.Mediator" Version="$version" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.0.0" />
  </ItemGroup>
</Project>
XML

cat > "$work/App/Program.cs" <<'CS'
using Microsoft.Extensions.DependencyInjection;
using Module;
using Sharp.Mediator;

var services = new ServiceCollection();
services.AddMediator(x => x.AddHandler<PingHandler>().AddMiddleware(typeof(Audit<,>)));
services.AddModule();

using var provider = services.BuildServiceProvider();
using var scope = provider.CreateScope();
Console.WriteLine(await scope.ServiceProvider.GetRequiredService<IMediator>().Request(new Ping("world")));
Console.WriteLine(await ModuleEndpoint.Call(scope.ServiceProvider));

public sealed record Ping(string Name) : IRequest<string>;

public sealed class PingHandler : IRequestHandler<Ping, string>
{
    public ValueTask<string> Handle(Ping message, CancellationToken cancellationToken) =>
        ValueTask.FromResult($"hello, {message.Name}");
}

public sealed class Audit<TMessage, TResponse> : IMiddleware<TMessage, TResponse> where TMessage : IMessage<TResponse>
{
    public ValueTask<TResponse> Handle(TMessage message, MessageHandle<TMessage, TResponse> next, CancellationToken cancellationToken) =>
        next(message, cancellationToken);
}
CS

# Gets the package only through Module, as a web project gets it through an application layer: the generator and the
# interceptors have to reach it that way too.
cat > "$work/Host/Host.csproj" <<XML
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../Module/Module.csproj" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.0.0" />
  </ItemGroup>
</Project>
XML

cat > "$work/Host/Program.cs" <<'CS'
using Microsoft.Extensions.DependencyInjection;
using Sharp.Mediator;

var services = new ServiceCollection();
services.AddMediator(x => x.AddHandler<HostPingHandler>());
using var provider = services.BuildServiceProvider();
Console.WriteLine(await provider.GetRequiredService<IMediator>().Request(new HostPing()));

public sealed record HostPing : IRequest<string>;

public sealed class HostPingHandler : IRequestHandler<HostPing, string>
{
    public ValueTask<string> Handle(HostPing message, CancellationToken cancellationToken) => ValueTask.FromResult("host");
}
CS

expected=$'hello, world\nmodule:1'
check() {
    local label="$1" actual="$2"
    if [ "$actual" != "$expected" ]; then
        echo "FAIL: $label printed unexpected output:" >&2
        printf '%s\n' "$actual" >&2
        exit 1
    fi
    echo "ok  : $label"
}

cd "$work"
rid="$(dotnet msbuild App/App.csproj -getProperty:NETCoreSdkRuntimeIdentifier)"

# The generated files go to a path relative to each project, under its obj/, which keeps them out of its sources. The
# property reaches Module too, and Module runs the same generator under the same file names, so in one shared folder a
# project that generated no interceptors would pass on the ones Module left there.
# Counted over the files' joined text, so several files add up, and a missing file or one without any gives 0 instead
# of a grep failure that would end the script before its FAIL message.
count_interceptors() {
    find "$1" -name 'SharpMediator.Interceptors.g.cs' -exec cat {} + | grep -c 'InterceptsLocation(' || true
}

generated=obj/generated
dotnet build App/App.csproj -c Release --nologo -v quiet \
    -p:EmitCompilerGeneratedFiles=true "-p:CompilerGeneratedFilesOutputPath=$generated"
check "JIT build" "$(dotnet App/bin/Release/net10.0/App.dll)"

# The projects above configure nothing, so these prove the package turns interceptors on by itself.
intercepted="$(count_interceptors "App/$generated")"
if [ "$intercepted" -eq 0 ]; then
    echo "FAIL: a project that configures nothing has no interceptors" >&2
    exit 1
fi
echo "ok  : interceptors on without configuration ($intercepted call site)"

dotnet publish App/App.csproj -c Release -r "$rid" --self-contained false -p:PublishReadyToRun=true -o publish --nologo -v quiet
check "ReadyToRun publish ($rid)" "$(dotnet publish/App.dll)"

# Built again incrementally, as a consumer's next build is, so the switch alone has to recompile the app.
cp App/bin/Release/net10.0/App.dll "$work/App.intercepted.dll"
generated=obj/generated-off
dotnet build App/App.csproj -c Release --nologo -v quiet -p:SharpMediatorInterceptors=false \
    -p:EmitCompilerGeneratedFiles=true "-p:CompilerGeneratedFilesOutputPath=$generated"
check "JIT build with interceptors off" "$(dotnet App/bin/Release/net10.0/App.dll)"
if cmp -s App/bin/Release/net10.0/App.dll "$work/App.intercepted.dll"; then
    echo "FAIL: an incremental build with SharpMediatorInterceptors=false kept the binary it had" >&2
    exit 1
fi
if [ -n "$(find "App/$generated" "Module/$generated" -name 'SharpMediator.Interceptors.g.cs')" ]; then
    echo "FAIL: SharpMediatorInterceptors=false still generated interceptors" >&2
    exit 1
fi
echo "ok  : SharpMediatorInterceptors=false turns interceptors off on an incremental build"

generated=obj/generated-host
dotnet build Host/Host.csproj -c Release --nologo -v quiet \
    -p:EmitCompilerGeneratedFiles=true "-p:CompilerGeneratedFilesOutputPath=$generated"
host="$(dotnet Host/bin/Release/net10.0/Host.dll)"
if [ "$host" != "host" ]; then
    echo "FAIL: a project that gets the package through a project reference printed: $host" >&2
    exit 1
fi
intercepted="$(count_interceptors "Host/$generated")"
if [ "$intercepted" -eq 0 ]; then
    echo "FAIL: a project that gets the package through a project reference has no interceptors" >&2
    exit 1
fi
echo "ok  : project reference consumer gets the generator and its interceptors"

# A build that skips analyzers still fails on a declaration the generator leaves out for an error. The package's build
# files hand the generator the two properties the SDK decides that from; nothing else in the repository builds through
# those files with analyzers off.
mkdir -p "$work/Broken"
cat > "$work/Broken/Broken.csproj" <<XML
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Sharp.Mediator" Version="$version" />
  </ItemGroup>
</Project>
XML

cat > "$work/Broken/Broken.cs" <<'CS'
using Sharp.Mediator;

namespace Broken;

public sealed record Dup : IRequest<int>;

public sealed class FirstHandler : IRequestHandler<Dup, int>
{
    public ValueTask<int> Handle(Dup message, CancellationToken cancellationToken) => ValueTask.FromResult(1);
}

public sealed class SecondHandler : IRequestHandler<Dup, int>
{
    public ValueTask<int> Handle(Dup message, CancellationToken cancellationToken) => ValueTask.FromResult(2);
}
CS

for skip in -p:RunAnalyzers=false -p:RunAnalyzersDuringBuild=false; do
    if output="$(dotnet build Broken/Broken.csproj -c Release --nologo -v quiet "$skip" 2>&1)"; then
        echo "FAIL: a build with $skip compiled two handlers for one request" >&2
        exit 1
    fi
    if ! grep -q 'error SMD005' <<<"$output"; then
        echo "FAIL: a build with $skip failed without SMD005:" >&2
        printf '%s\n' "$output" >&2
        exit 1
    fi
    echo "ok  : a build with $skip still reports SMD005"
done

echo "ALL CONSUMER CHECKS PASS"
