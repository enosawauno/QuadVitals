namespace QuadVitals.Core
{
    /// <summary>
    /// The one place the product version is written down. Everything user-visible reads this
    /// constant (About page, feedback metadata, tray diagnostics) and so do the assembly
    /// attributes in <c>src/SharedAssemblyInfo.cs</c>, which every managed binary compiles in.
    /// Without a single source the "file properties say 0.0.0.0, About page says something else"
    /// drift is only a matter of time.
    ///
    /// Three numeric segments on purpose: "0.9.0" is the version, not "0.9.0.0". Roslyn accepts
    /// three segments for both AssemblyVersion and AssemblyFileVersion, so the readable form is
    /// also the one stored in the binaries. The only place that needs four numbers is the Win32
    /// VERSIONINFO resource field of the native band DLL, and the build script derives that from
    /// this string instead of keeping a second copy.
    /// </summary>
    public static class ProductVersion
    {
        /// <summary>Readable version, also what the feedback upload reports as <c>appVersion</c>.</summary>
        public const string SemVer = "0.9.5";
    }
}
