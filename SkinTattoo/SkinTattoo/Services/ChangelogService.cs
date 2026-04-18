using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Dalamud.Plugin.Services;
using Newtonsoft.Json.Linq;

namespace SkinTattoo.Services;

public sealed class ChangelogEntry
{
    public string Version { get; init; } = "";
    public string Date { get; init; } = "";
    public IReadOnlyList<string> En { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Zh { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> BulletsFor(string languageCode)
        => languageCode.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? Zh : En;
}

public sealed class ChangelogService
{
    public IReadOnlyList<ChangelogEntry> Entries { get; }

    public ChangelogService(IPluginLog log)
    {
        Entries = Load(log);
    }

    private static IReadOnlyList<ChangelogEntry> Load(IPluginLog log)
    {
        var asm = typeof(ChangelogService).Assembly;
        var resourceName = FindResourceName(asm, "Changelog.json");
        if (resourceName == null)
        {
            log.Warning("Changelog.json embedded resource not found");
            return Array.Empty<ChangelogEntry>();
        }

        try
        {
            using var stream = asm.GetManifestResourceStream(resourceName)!;
            using var reader = new StreamReader(stream);
            var root = JObject.Parse(reader.ReadToEnd());
            var list = new List<ChangelogEntry>();
            foreach (var v in (root["versions"] as JArray) ?? new JArray())
                list.Add(new ChangelogEntry
                {
                    Version = v.Value<string>("version") ?? "",
                    Date = v.Value<string>("date") ?? "",
                    En = ToStringArray(v["en"]),
                    Zh = ToStringArray(v["zh"]),
                });
            return list;
        }
        catch (Exception ex)
        {
            log.Error(ex, "Failed to parse Changelog.json");
            return Array.Empty<ChangelogEntry>();
        }
    }

    private static string? FindResourceName(Assembly asm, string suffix)
    {
        foreach (var n in asm.GetManifestResourceNames())
            if (n.EndsWith(suffix, StringComparison.Ordinal))
                return n;
        return null;
    }

    private static string[] ToStringArray(JToken? tok)
    {
        if (tok is not JArray arr) return Array.Empty<string>();
        var result = new string[arr.Count];
        for (int i = 0; i < arr.Count; i++)
            result[i] = arr[i].Value<string>() ?? "";
        return result;
    }
}
