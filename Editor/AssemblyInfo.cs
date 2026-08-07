using System.Runtime.CompilerServices;

// The isolation services are internal; the Editor Test Runner suite lives in a
// separate assembly and needs to reach them.
[assembly: InternalsVisibleTo("Xestrel.Editor.Tests")]
