using System.Reflection;
using System.Runtime.Loader;
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ClassIsland.Core.Models.Plugin;
using ClassIsland.Services;

namespace ClassIsland;

/// <summary>
/// 为插件加载提供隔离的 <see cref="AssemblyLoadContext"/> 实现。<para/>
/// 负责从插件依赖项解析托管与非托管依赖项，同时严格保证宿主共享程序集单例加载。
/// </summary>
public class PluginLoadContext : AssemblyLoadContext
{
    private readonly string _pluginDirectory;
    private readonly AssemblyDependencyResolver? _resolver;

    public PluginLoadContext(PluginInfo info, string fullPath, bool suppressMacPluginLoader = false) 
        : base($"ClassIsland.PluginLoadContext[{info.Manifest.Id}]", isCollectible: true)
    {
        Info = info;
        _pluginDirectory = Path.GetDirectoryName(fullPath) ?? "";
        _resolver = TryCreateResolver(fullPath);
    }

    private static AssemblyDependencyResolver? TryCreateResolver(string fullPath)
    {
        try
        {
            return new AssemblyDependencyResolver(fullPath);
        }
        catch
        {
            // 在 macOS 原生打包（Microsoft.macOS.Sdk）运行时中，hostfxr/corehost_main 未被显式初始化，
            // AssemblyDependencyResolver 会抛出 InvalidOperationException。
            // 此时回退到自定义目录解析逻辑。
            return null;
        }
    }

    /// <summary>
    /// 插件信息与清单引用，用于根据声明的依赖项查找并委托到其它插件的加载上下文。
    /// </summary>
    public PluginInfo Info { get; }

    private static IReadOnlyList<string> WinRTDeps { get; } = [
        "WinRT.Runtime",
        "Microsoft.Windows.SDK.NET"
    ];

    private static readonly HashSet<string> HostAssemblyExactNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ClassIsland",
        "ClassIsland.Core",
        "ClassIsland.Shared",
        "ClassIsland.Shared.IPC",
        "ClassIsland.Platforms.Abstractions",
        "ClassIsland.Platforms.Windows",
        "ClassIsland.Platforms.Linux",
        "ClassIsland.Platforms.MacOs",
        "ClassIsland.Desktop",
        "ClassIsland.Launcher",
        "ClassIsland.PluginSdk"
    };

    private static readonly HashSet<string> HostAssemblyPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Avalonia",
        "FluentAvalonia",
        "CommunityToolkit.",
        "Microsoft.",
        "System.",
        "System",
        "netstandard",
        "mscorlib",
        "YamlDotNet",
        "Newtonsoft.Json",
        "Google.Protobuf",
        "Sentry",
        "Material.Icons"
    };

    private static bool IsHostAssembly(string? assemblyName)
    {
        if (string.IsNullOrEmpty(assemblyName))
            return false;

        // 1. 完全匹配已知宿主核心程序集
        if (HostAssemblyExactNames.Contains(assemblyName))
            return true;

        // 2. 检查前缀或完全匹配基础框架程序集
        foreach (var prefix in HostAssemblyPrefixes)
        {
            if (assemblyName.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                assemblyName.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // 3. 检查是否已经在默认主上下文加载
        return Default.Assemblies.Any(a => string.Equals(a.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 在需要加载程序集时被调用。优先委托给宿主共享程序集，接着解析插件依赖项及插件目录程序集。
    /// </summary>
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // 1. WinRT 依赖使用宿主实现
        if (assemblyName.Name != null && WinRTDeps.Contains(assemblyName.Name))
        {
            return null;
        }

        // 2. 宿主核心与框架程序集直接委托给默认上下文，确保类型一致性
        if (IsHostAssembly(assemblyName.Name))
        {
            return null;
        }

        // 3. 尝试从声明的依赖插件上下文查找
        foreach (var dep in Info.Manifest.Dependencies)
        {
            if (!PluginService.PluginLoadContexts.TryGetValue(dep.Id, out var context))
            {
                continue;
            }

            var assembly = context.LoadFromAssemblyName(assemblyName);
            if (assembly != null)
            {
                return assembly;
            }
        }

        // 4. 优先通过 .deps.json 标准依赖解析器解析（若可用）
        if (_resolver != null)
        {
            try
            {
                var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
                if (assemblyPath != null && File.Exists(assemblyPath))
                {
                    return LoadFromAssemblyPath(assemblyPath);
                }
            }
            catch
            {
                // ignore
            }
        }

        // 5. 回退到插件根目录搜索
        if (!string.IsNullOrEmpty(_pluginDirectory) && !string.IsNullOrEmpty(assemblyName.Name))
        {
            var fallbackDll = Path.Combine(_pluginDirectory, assemblyName.Name + ".dll");
            if (File.Exists(fallbackDll))
            {
                return LoadFromAssemblyPath(fallbackDll);
            }

            // 搜索 runtimes 目录下的托管依赖
            var os = OperatingSystem.IsMacOS() ? "osx" :
                     OperatingSystem.IsLinux() ? "linux" :
                     OperatingSystem.IsWindows() ? "win" : "";

            var subPaths = new[]
            {
                Path.Combine(_pluginDirectory, "runtimes", os, "lib", "net8.0", assemblyName.Name + ".dll"),
                Path.Combine(_pluginDirectory, "runtimes", "any", "lib", "net8.0", assemblyName.Name + ".dll"),
            };
            foreach (var sp in subPaths)
            {
                if (File.Exists(sp))
                {
                    return LoadFromAssemblyPath(sp);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 解析并加载插件的非托管（本地）库，支持跨平台约定与 runtimes 架构目录。
    /// </summary>
    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        // 1. 尝试使用标准解析器（若可用）
        if (_resolver != null)
        {
            try
            {
                var libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
                if (libraryPath != null && File.Exists(libraryPath))
                {
                    return LoadUnmanagedDllFromPath(libraryPath);
                }
            }
            catch
            {
                // ignore
            }
        }

        // 2. 回退跨平台多路径搜索
        var fallbackPath = ResolveUnmanagedDllFallback(unmanagedDllName);
        if (fallbackPath != null && File.Exists(fallbackPath))
        {
            return LoadUnmanagedDllFromPath(fallbackPath);
        }

        return IntPtr.Zero;
    }

    private string? ResolveUnmanagedDllFallback(string unmanagedDllName)
    {
        if (string.IsNullOrEmpty(_pluginDirectory))
            return null;

        var os = OperatingSystem.IsMacOS() ? "osx" :
                 OperatingSystem.IsLinux() ? "linux" :
                 OperatingSystem.IsWindows() ? "win" : "";

        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            Architecture.X86 => "x86",
            _ => null
        };

        var searchPaths = new List<string>
        {
            _pluginDirectory
        };

        if (!string.IsNullOrEmpty(os))
        {
            searchPaths.Add(Path.Combine(_pluginDirectory, "runtimes", os, "native"));
            if (arch != null)
            {
                searchPaths.Add(Path.Combine(_pluginDirectory, "runtimes", $"{os}-{arch}", "native"));
            }
        }

        var extensions = OperatingSystem.IsMacOS() ? new[] { ".dylib", "" } :
                         OperatingSystem.IsLinux() ? new[] { ".so", "" } :
                         new[] { ".dll", "" };

        foreach (var path in searchPaths)
        {
            if (!Directory.Exists(path)) continue;

            foreach (var ext in extensions)
            {
                var candidates = new[]
                {
                    Path.Combine(path, $"lib{unmanagedDllName}{ext}"),
                    Path.Combine(path, $"{unmanagedDllName}{ext}")
                };

                foreach (var candidate in candidates)
                {
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
        }

        return null;
    }
}
