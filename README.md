# Excovery

Excovery is a Roslyn analyzer for XML exception documentation. The first rule,
`EXC001`, reports an explicit `throw` when its statically known exception type
does not appear in an `<exception cref="...">` tag on the containing member.
Bare rethrows use their enclosing catch type. The analyzer also checks calls to
methods with `<exception>` tags and requires the calling member to document
each exception that can escape.

`EXC002` traces exceptions from lambdas passed to source methods whose delegate
parameters are invoked, including through chains of source methods that forward
delegate parameters, and from lambdas stored in a local delegate that is invoked
or passed onward. When a traced invocation can let an
exception escape without documentation, EXC002 appears both at the invocation
and at the delegate argument at the call site. The message names the method
whose invocation is missing the exception documentation. It also supports a
local collection initialized with lambdas and passed to a source method that invokes its `IEnumerable<Delegate>`
parameter in a `foreach`. This is a same-compilation approximation: it does not
infer delegate reassignment, method groups, opaque collection contents, or
behavior inside external methods.

At call sites, an unfiltered catch handles its declared exception type and
derived types; a bare `catch` handles all documented exception types. Filtered
catches are treated as potentially letting the exception escape.

EXC002 searches can be limited in `.editorconfig`:

```editorconfig
[*.cs]
excovery.max_outward_delegate_search_count = 1
excovery.max_inward_delegate_search_count = 1
excovery.max_inward_delegate_search_hop_count = 3
```

The outward limit caps EXC002 warnings at each delegate invocation location;
the inward limit caps warnings at each delegate argument location. These values
count warnings, not method hops. An unset value or zero means no warning limit.
Once a location reaches its limit, additional warnings there are suppressed.

`excovery.max_inward_delegate_search_hop_count` limits how many source methods
may forward a delegate before the analyzer stops looking for its invocation.
Its fallback is three hops when unset or invalid; zero means unlimited. A limit
of one follows one forwarding method before looking for the delegate invocation.

## Use from the sample project

Open `CSExtensionPluginTest` as a .NET project in Neovim. Its project file
references this project as an analyzer, so `roslyn_ls` loads the analyzer when
it loads the project. No special Neovim configuration is required beyond the
existing Roslyn language server setup.

The solution file is at the root of this folder; analyzer source lives in the
`Excovery` project subfolder.

The sample includes documented and undocumented throws, a bare rethrow, and
calls with all, some, or none of their documented exception types caught.
`EXC001` is a warning and appears on each missing throw or call-site
documentation requirement.

The `Add missing <exception> documentation` code action adds empty tags for
every currently missing exception in the affected method, not just the
diagnostic under the cursor. Cref names use the shortest form valid at the
method's location.

To consume the analyzer from another project, add the same reference:

```xml
<ProjectReference Include="path/to/Excovery/Excovery.csproj"
                  OutputItemType="Analyzer"
                  ReferenceOutputAssembly="false" />
```
