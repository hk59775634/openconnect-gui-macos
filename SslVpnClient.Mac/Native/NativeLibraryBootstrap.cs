using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace SslVpnClient.Mac.Native;

/// <summary>
/// 加载随应用分发的 libopenconnect / oc_progress_bridge（@loader_path）。
/// </summary>
public static class NativeLibraryBootstrap
{
    private const string LogicalLibName = "libopenconnect";
    private const string ProgressBridgeDll = "oc_progress_bridge";

    private static string? _resolvedLibPath;
    private static string? _resolvedProgressBridgePath;
    private static IntPtr _nativeModule;
    private static IntPtr _progressBridgeModule;
    private static bool _initialized;

    public static string? ResolvedLibraryPath => _resolvedLibPath;

    public static void Initialize(ILogger? logger = null)
    {
        if (_initialized)
        {
            return;
        }

        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var searchDirs = GetSearchDirectories(baseDir);

        NativeLibrary.SetDllImportResolver(typeof(OpenConnectNative).Assembly, ResolveDllImport);

        _resolvedLibPath = FindLibrary(searchDirs, "libopenconnect*.dylib", "libopenconnect.dylib");
        _resolvedProgressBridgePath = FindExact(searchDirs, "oc_progress_bridge.dylib");

        if (_resolvedLibPath != null)
        {
            try
            {
                _nativeModule = NativeLibrary.Load(_resolvedLibPath);
                var initResult = OpenConnectNative.openconnect_init_ssl();
                if (initResult != 0)
                {
                    throw new InvalidOperationException($"openconnect_init_ssl 失败 (code={initResult})");
                }

                logger?.LogInformation("已加载 OpenConnect: {Path}", _resolvedLibPath);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "OpenConnect 原生库初始化失败");
                _nativeModule = IntPtr.Zero;
            }
        }
        else
        {
            logger?.LogWarning("未找到 libopenconnect.dylib，请运行 scripts/vendor-macos-native.sh");
        }

        if (_resolvedProgressBridgePath != null)
        {
            try
            {
                _progressBridgeModule = NativeLibrary.Load(_resolvedProgressBridgePath);
                logger?.LogInformation("已加载 progress 桥接: {Path}", _resolvedProgressBridgePath);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "oc_progress_bridge 加载失败");
                _progressBridgeModule = IntPtr.Zero;
            }
        }

        _initialized = true;
    }

    public static void EnsureOpenConnectAvailable()
    {
        Initialize();

        if (_resolvedLibPath == null || !File.Exists(_resolvedLibPath) || _nativeModule == IntPtr.Zero)
        {
            throw new DllNotFoundException(
                "未找到内置 libopenconnect。请在开发机运行 scripts/vendor-macos-native.sh，或使用含 Native 库的发布包。");
        }

        if (_progressBridgeModule == IntPtr.Zero)
        {
            throw new DllNotFoundException(
                "未找到 oc_progress_bridge.dylib。请运行 scripts/vendor-macos-native.sh。");
        }
    }

    public static string? FindVpncScript()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        foreach (var dir in GetSearchDirectories(baseDir))
        {
            var p = Path.Combine(dir, "vpnc-script");
            if (File.Exists(p))
            {
                return p;
            }
        }

        // Installed helper copy
        var installed = "/Library/OpenConnectGui/vpnc-script";
        return File.Exists(installed) ? installed : null;
    }

    private static IEnumerable<string> GetSearchDirectories(string baseDir)
    {
        var rid = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
        yield return "/Library/OpenConnectGui/lib";
        yield return Path.Combine(baseDir, "Native", "lib", rid);
        yield return Path.Combine(baseDir, "Native");
        yield return Path.Combine(baseDir, "runtimes", rid, "native");
        yield return baseDir;

        // Source-tree fallback when running from bin/
        var projNative = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "Native"));
        if (Directory.Exists(projNative))
        {
            yield return Path.Combine(projNative, "lib", rid);
            yield return projNative;
        }
    }

    private static string? FindLibrary(IEnumerable<string> dirs, string glob, string exactName)
    {
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            var exact = Path.Combine(dir, exactName);
            if (File.Exists(exact))
            {
                return exact;
            }

            var match = Directory.GetFiles(dir, glob).OrderByDescending(f => f).FirstOrDefault();
            if (match != null)
            {
                return match;
            }
        }

        return null;
    }

    private static string? FindExact(IEnumerable<string> dirs, string fileName)
    {
        foreach (var dir in dirs)
        {
            var path = Path.Combine(dir, fileName);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static IntPtr ResolveDllImport(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (string.Equals(libraryName, ProgressBridgeDll, StringComparison.OrdinalIgnoreCase) ||
            libraryName.Contains("oc_progress_bridge", StringComparison.OrdinalIgnoreCase))
        {
            if (_progressBridgeModule != IntPtr.Zero)
            {
                return _progressBridgeModule;
            }

            if (_resolvedProgressBridgePath != null)
            {
                _progressBridgeModule = NativeLibrary.Load(_resolvedProgressBridgePath, assembly, searchPath);
                return _progressBridgeModule;
            }

            return IntPtr.Zero;
        }

        if (!libraryName.Contains("openconnect", StringComparison.OrdinalIgnoreCase))
        {
            return IntPtr.Zero;
        }

        if (_nativeModule != IntPtr.Zero)
        {
            return _nativeModule;
        }

        if (_resolvedLibPath != null)
        {
            _nativeModule = NativeLibrary.Load(_resolvedLibPath, assembly, searchPath);
            return _nativeModule;
        }

        return IntPtr.Zero;
    }
}
