using System.Runtime.CompilerServices;

// The HID layer is deliberately internal: raw P/Invoke structs and report buffers are not
// a public contract. The tools in this solution are built alongside it and share that
// internal surface rather than forcing a premature public API.
// These are ASSEMBLY names, not project names, and the application's assembly is "Dovetail"
// while its project is "Dovetail.App". The entry here said "Dovetail.App" and therefore
// granted nothing to anybody: the WPF app had no access to Core's internals at all. It went
// unnoticed because the app had only ever used the public surface, and the first call that
// needed an internal one failed to compile rather than silently misbehaving. The same typo
// was present before the Stage 6 rename, as "Bridge.App" against an assembly named "Bridge".
[assembly: InternalsVisibleTo("dovetail-diag")]
[assembly: InternalsVisibleTo("dovetail-engine")]
[assembly: InternalsVisibleTo("Dovetail")]
[assembly: InternalsVisibleTo("Dovetail.Core.Tests")]
