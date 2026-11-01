using System.Reflection;

// Assembly metadata for every managed binary in the product (Core / UI / Settings / Agent).
// The value comes from QuadVitals.Core.ProductVersion, so the About page, the feedback metadata
// and the file properties cannot disagree; each build script compiles this file in by path.
[assembly: AssemblyVersion(QuadVitals.Core.ProductVersion.SemVer)]
[assembly: AssemblyFileVersion(QuadVitals.Core.ProductVersion.SemVer)]
[assembly: AssemblyInformationalVersion(QuadVitals.Core.ProductVersion.SemVer)]
