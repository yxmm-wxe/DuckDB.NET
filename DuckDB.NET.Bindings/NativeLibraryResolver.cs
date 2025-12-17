using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

#if !NETSTANDARD2_0
using System.Runtime.CompilerServices;
#endif

namespace DuckDB.NET.Native;

/// <summary>
/// Handles custom RID resolution for native DuckDB library loading.
/// Supports linux-x64-glibc217 for systems with older GLIBC (e.g., CentOS 7 with GLIBC 2.17).
/// </summary>
public static class NativeLibraryResolver
{
    private const string DuckDbLibraryName = "duckdb";
    private const string LibDuckDbLibraryName = "libduckdb";

    // Environment variable to force using glibc217 build
    private const string UseGlibc217EnvVar = "DUCKDB_NET_USE_GLIBC217";

    // Minimum GLIBC version that supports standard linux-x64 build (2.28 based on DuckDB requirements)
    private static readonly Version MinGlibcVersion = new Version(2, 28);

#if !NETSTANDARD2_0
    private static bool initialized;
    private static readonly object initLock = new object();
#endif

#if !NETSTANDARD2_0
    /// <summary>
    /// Initializes the native library resolver. This is called automatically via ModuleInitializer.
    /// </summary>
#pragma warning disable CA2255 // The 'ModuleInitializer' attribute should not be used in libraries - intentionally used here for automatic resolver registration
    [ModuleInitializer]
#pragma warning restore CA2255
    public static void Initialize()
    {
        RegisterResolver();
    }
#endif

    /// <summary>
    /// Registers the custom DLL import resolver. Can be called manually for netstandard2.0 targets.
    /// </summary>
    public static void RegisterResolver()
    {
#if !NETSTANDARD2_0
        lock (initLock)
        {
            if (initialized) return;

            NativeLibrary.SetDllImportResolver(typeof(NativeLibraryResolver).Assembly, ResolveDuckDbLibrary);
            initialized = true;
        }
#endif
    }

#if !NETSTANDARD2_0
    private static IntPtr ResolveDuckDbLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        // Only handle duckdb library
        if (!libraryName.Equals(DuckDbLibraryName, StringComparison.OrdinalIgnoreCase) &&
            !libraryName.Equals(LibDuckDbLibraryName, StringComparison.OrdinalIgnoreCase))
        {
            return IntPtr.Zero;
        }

        // Try custom RID path first for Linux x64
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
            RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            if (ShouldUseGlibc217Build())
            {
                var glibc217Path = GetNativeLibraryPath("linux-x64-glibc217");
                if (glibc217Path != null && NativeLibrary.TryLoad(glibc217Path, out var handle))
                {
                    return handle;
                }
            }
        }

        // Fall back to default resolution
        return IntPtr.Zero;
    }
#endif

    /// <summary>
    /// Determines whether to use the glibc217 build based on environment variable or GLIBC version detection.
    /// </summary>
    public static bool ShouldUseGlibc217Build()
    {
        // Check environment variable first
        var envValue = Environment.GetEnvironmentVariable(UseGlibc217EnvVar);
        if (!string.IsNullOrEmpty(envValue))
        {
            return envValue.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                   envValue.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        // Auto-detect based on GLIBC version
        var glibcVersion = GetGlibcVersion();
        if (glibcVersion != null && glibcVersion < MinGlibcVersion)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Gets the GLIBC version on Linux systems.
    /// </summary>
    internal static Version? GetGlibcVersion()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return null;
        }

        try
        {
            // Try to get version from gnu_get_libc_version
            var versionPtr = gnu_get_libc_version();
            if (versionPtr != IntPtr.Zero)
            {
                var versionString = Marshal.PtrToStringAnsi(versionPtr);
                if (!string.IsNullOrEmpty(versionString) && Version.TryParse(versionString, out var version))
                {
                    return version;
                }
            }
        }
        catch
        {
            // Ignore exceptions - may not be glibc
        }

        return null;
    }

    [DllImport("libc.so.6", EntryPoint = "gnu_get_libc_version")]
    private static extern IntPtr gnu_get_libc_version();

    /// <summary>
    /// Gets the full path to the native library for the specified RID.
    /// </summary>
    internal static string? GetNativeLibraryPath(string rid)
    {
        var assemblyLocation = typeof(NativeLibraryResolver).Assembly.Location;
        if (string.IsNullOrEmpty(assemblyLocation))
        {
            return null;
        }

        var assemblyDir = Path.GetDirectoryName(assemblyLocation);
        if (string.IsNullOrEmpty(assemblyDir))
        {
            return null;
        }

        // Check relative to assembly directory: runtimes/{rid}/native/libduckdb.so
        var libraryPath = Path.Combine(assemblyDir, "runtimes", rid, "native", "libduckdb.so");
        if (File.Exists(libraryPath))
        {
            return libraryPath;
        }

        // Check one level up (for development scenarios)
        var parentDir = Path.GetDirectoryName(assemblyDir);
        if (!string.IsNullOrEmpty(parentDir))
        {
            libraryPath = Path.Combine(parentDir, "runtimes", rid, "native", "libduckdb.so");
            if (File.Exists(libraryPath))
            {
                return libraryPath;
            }
        }

        return null;
    }
}
