// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text.Json;
using Docfx.Common;
using Docfx.Exceptions;
using Docfx.Plugins;
using Microsoft.Build.Locator;
using Newtonsoft.Json.Linq;
using YamlDotNet.Serialization;

namespace Docfx.Dotnet;

/// <summary>
/// Provides access to a .NET API definitions and their associated documentation.
/// </summary>
public static partial class DotnetApiCatalog
{
    /// <summary>
    /// Generates metadata reference YAML files using docfx.json config.
    /// </summary>
    /// <param name="configPath">The path to docfx.json config file.</param>
    /// <returns>A task to await for build completion.</returns>
    public static Task GenerateManagedReferenceYamlFiles(string configPath)
    {
        return GenerateManagedReferenceYamlFiles(configPath, new());
    }

    /// <summary>
    /// Generates metadata reference YAML files using docfx.json config.
    /// </summary>
    /// <param name="configPath">The path to docfx.json config file.</param>
    /// <returns>A task to await for build completion.</returns>
    public static async Task GenerateManagedReferenceYamlFiles(string configPath, DotnetApiOptions options)
    {
        var consoleLogListener = new ConsoleLogListener();
        Logger.RegisterListener(consoleLogListener);

        try
        {
            var configDirectory = Path.GetDirectoryName(Path.GetFullPath(configPath));

            var config = JObject.Parse(File.ReadAllText(configPath));
            if (config.TryGetValue("metadata", out var value))
                await Exec(value.ToObject<MetadataJsonConfig>(JsonUtility.DefaultSerializer.Value), options, configDirectory);
        }
        finally
        {
            Logger.Flush();
            Logger.PrintSummary();
            Logger.UnregisterAllListeners();
        }
    }

    internal static async Task Exec(MetadataJsonConfig config, DotnetApiOptions options, string configDirectory, string outputDirectory = null)
    {
        var stopwatch = Stopwatch.StartNew();

        EnsureMSBuildLocator();

        try
        {
            string originalGlobalNamespaceId = VisitorHelper.GlobalNamespaceId;

            EnvironmentContext.SetBaseDirectory(configDirectory);

            foreach (var item in config)
            {
                VisitorHelper.GlobalNamespaceId = item.GlobalNamespaceId;
                EnvironmentContext.SetGitFeaturesDisabled(item.DisableGitFeatures);

                await Build(ConvertConfig(item, configDirectory, outputDirectory), options);
            }

            VisitorHelper.GlobalNamespaceId = originalGlobalNamespaceId;
        }
        finally
        {
            EnvironmentContext.Clean();
        }

        Logger.LogVerbose($".NET API done in {stopwatch.Elapsed}");

        async Task Build(ExtractMetadataConfig config, DotnetApiOptions options)
        {
            var assemblies = await Compile(config, options);

            switch (config.OutputFormat)
            {
                case MetadataOutputFormat.Markdown:
#if NET7_0_OR_GREATER
                    CreatePages(WriteMarkdown, assemblies, config, options);

                    void WriteMarkdown(string outputFolder, string id, Build.ApiPage.ApiPage apiPage)
                    {
                        File.WriteAllText(Path.Combine(outputFolder, $"{id}.md"), Docfx.Build.ApiPage.ApiPageMarkdownTemplate.Render(apiPage));
                    }
#else
                    Logger.LogError($"Markdown output format is only supported for docfx built against .NET 7 or greater.");
#endif
                    break;

                case MetadataOutputFormat.ApiPage:
#if NET7_0_OR_GREATER
                    var serializer = new DeserializerBuilder().WithAttemptingUnquotedStringTypeDeserialization().Build();
                    CreatePages(WriteYaml, assemblies, config, options);

                    void WriteYaml(string outputFolder, string id, Build.ApiPage.ApiPage apiPage)
                    {
                        var json = JsonSerializer.Serialize(apiPage, Docfx.Build.ApiPage.ApiPage.JsonSerializerOptions);
                        var obj = serializer.Deserialize(json);
                        YamlUtility.Serialize(Path.Combine(outputFolder, $"{id}.yml"), obj, "YamlMime:ApiPage");
                    }
#else
                    Logger.LogError($"ApiPage output format is only supported for docfx built against .NET 7 or greater.");
#endif
                    break;

                case MetadataOutputFormat.Mref:
                    CreateManagedReference(assemblies, config, options);
                    break;
            }
        }
    }

    private static void EnsureMSBuildLocator()
    {
        try
        {
            if (!MSBuildLocator.IsRegistered)
            {
                var vs = MSBuildLocator.RegisterDefaults() ?? throw new ExtractMetadataException(
                    $"Cannot find a supported .NET Core SDK. Install .NET Core SDK {Environment.Version.Major}.{Environment.Version.Minor}.x to build .NET API docs.");

                Logger.LogInfo($"Using {vs.Name} {vs.Version}");
            }
        }
        catch (Exception e)
        {
            throw new ExtractMetadataException(e.Message, e);
        }
    }

    private static ExtractMetadataConfig ConvertConfig(MetadataJsonItemConfig configModel, string configDirectory, string outputDirectory)
    {
        var projects = configModel.Src;
        var references = configModel.References;

        var outputFolder = Path.GetFullPath(Path.Combine(
            string.IsNullOrEmpty(outputDirectory) ? Path.Combine(configDirectory, configModel.Output ?? "") : outputDirectory,
            configModel.Dest ?? ""));

        var expandedFiles = GlobUtility.ExpandFileMapping(EnvironmentContext.BaseDirectory, projects);
        var expandedReferences = GlobUtility.ExpandFileMapping(EnvironmentContext.BaseDirectory, references);

        return new ExtractMetadataConfig
        {
            ShouldSkipMarkup = configModel?.ShouldSkipMarkup ?? false,
            FilterConfigFile = configModel?.Filter is null ? null : Path.GetFullPath(Path.Combine(EnvironmentContext.BaseDirectory, configModel.Filter)),
            IncludePrivateMembers = configModel?.IncludePrivateMembers ?? false,
            GlobalNamespaceId = configModel?.GlobalNamespaceId,
            MSBuildProperties = configModel?.Properties,
            OutputFormat = configModel?.OutputFormat ?? default,
            OutputFolder = outputFolder,
            CodeSourceBasePath = configModel?.CodeSourceBasePath,
            DisableDefaultFilter = configModel?.DisableDefaultFilter ?? false,
            DisableGitFeatures = configModel?.DisableGitFeatures ?? false,
            NoRestore = configModel?.NoRestore ?? false,
            NamespaceLayout = configModel?.NamespaceLayout ?? default,
            MemberLayout = configModel?.MemberLayout ?? default,
            EnumSortOrder = configModel?.EnumSortOrder ?? default,
            AllowCompilationErrors = configModel?.AllowCompilationErrors ?? false,
            Files = expandedFiles.Items.SelectMany(s => s.Files).ToList(),
            References = expandedReferences?.Items.SelectMany(s => s.Files).ToList(),
        };
    }
}
