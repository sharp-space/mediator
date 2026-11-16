#!/usr/bin/env bash
# Asserts the packed .nupkg files actually contain what they promise. The analyzer slot is the critical
# one: a package missing the generator only fails at the consumer's runtime ("No generated mediator
# registrar was found") or their NativeAOT publish — the worst possible places to find out.
set -euo pipefail

packages_dir="${1:?usage: verify-packages.sh <packages-dir>}"

require() {
    local archive="$1" entry="$2"
    # No `grep -q`: its early exit would SIGPIPE unzip, which pipefail then reports as a failure.
    if ! unzip -Z1 "$archive" | grep -Fx "$entry" >/dev/null; then
        echo "FAIL: $(basename "$archive") is missing '$entry'" >&2
        exit 1
    fi
    echo "ok  : $(basename "$archive") contains $entry"
}

# NB: a bare *.nupkg glob would also match *.snupkg (the 's' is absorbed by the wildcard), and
# Sharp.Mediator.* would absorb the satellite package names — anchor on the version digit. Exact-one
# validation also rejects stale artifacts from a prior version in a reused output directory.
find_exact_package() {
    local name="$1" kind="$2" pattern
    local matches=()
    if [ "$kind" = "symbols" ]; then
        pattern="$name.[0-9]*.snupkg"
    else
        pattern="$name.[0-9]*.nupkg"
    fi

    if [ "$kind" = "symbols" ]; then
        while IFS= read -r match; do
            matches+=("$match")
        done < <(find "$packages_dir" -type f -name "$pattern" | sort)
    else
        while IFS= read -r match; do
            matches+=("$match")
        done < <(find "$packages_dir" -type f -name "$pattern" ! -name '*.snupkg' | sort)
    fi

    if [ "${#matches[@]}" -ne 1 ]; then
        echo "FAIL: expected exactly one $kind package for $name, found ${#matches[@]}" >&2
        printf '  %s\n' "${matches[@]}" >&2
        exit 1
    fi

    printf '%s\n' "${matches[0]}"
}

core_pkg=$(find_exact_package "Sharp.Mediator" "runtime")
responses_pkg=$(find_exact_package "Sharp.Mediator.Responses" "runtime")
validation_pkg=$(find_exact_package "Sharp.Mediator.Validation" "runtime")
httpresults_pkg=$(find_exact_package "Sharp.Mediator.HttpResults" "runtime")

source_generator_packages=()
while IFS= read -r match; do
    source_generator_packages+=("$match")
done < <(find "$packages_dir" -type f \( -name 'Sharp.Mediator.SourceGenerator.*.nupkg' -o -name 'Sharp.Mediator.SourceGenerator.*.snupkg' \) | sort)
if [ "${#source_generator_packages[@]}" -ne 0 ]; then
    echo "FAIL: Sharp.Mediator.SourceGenerator must ship only inside the core analyzer slot, not as a package" >&2
    printf '  %s\n' "${source_generator_packages[@]}" >&2
    exit 1
fi
echo "ok  : no standalone Sharp.Mediator.SourceGenerator package"

require "$core_pkg" "lib/net10.0/Sharp.Mediator.dll"
require "$core_pkg" "lib/net10.0/Sharp.Mediator.xml"
require "$core_pkg" "analyzers/dotnet/cs/Sharp.Mediator.SourceGenerator.dll"
require "$core_pkg" "buildTransitive/Sharp.Mediator.targets"
require "$core_pkg" "README.md"
require "$core_pkg" "icon.png"

require "$responses_pkg" "lib/net10.0/Sharp.Mediator.Responses.dll"
require "$responses_pkg" "lib/net10.0/Sharp.Mediator.Responses.xml"
require "$responses_pkg" "README.md"
require "$responses_pkg" "icon.png"

require "$validation_pkg" "lib/net10.0/Sharp.Mediator.Validation.dll"
require "$validation_pkg" "lib/net10.0/Sharp.Mediator.Validation.xml"
require "$validation_pkg" "README.md"
require "$validation_pkg" "icon.png"

require "$httpresults_pkg" "lib/net10.0/Sharp.Mediator.HttpResults.dll"
require "$httpresults_pkg" "lib/net10.0/Sharp.Mediator.HttpResults.xml"
require "$httpresults_pkg" "README.md"
require "$httpresults_pkg" "icon.png"

# Symbol packages must also be unique for all four.
find_exact_package "Sharp.Mediator" "symbols" >/dev/null
find_exact_package "Sharp.Mediator.Responses" "symbols" >/dev/null
find_exact_package "Sharp.Mediator.Validation" "symbols" >/dev/null
find_exact_package "Sharp.Mediator.HttpResults" "symbols" >/dev/null
echo "ok  : exactly one symbol package present for each package ID"

echo "ALL PACKAGE CHECKS PASS"
