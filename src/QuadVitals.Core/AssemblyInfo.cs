using System.Runtime.CompilerServices;

// The dependency-free test runner lives in its own exe and needs the little-endian
// read/write helpers to verify the IPC layout byte by byte.
[assembly: InternalsVisibleTo("QuadVitals.Core.Tests")]
