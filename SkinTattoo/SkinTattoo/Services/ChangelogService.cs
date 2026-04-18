using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Dalamud.Plugin.Services;
using Newtonsoft.Json.Linq;

namespace SkinTattoo.Services;

public sealed class ChangelogLink
{
    public string Label { get; init; } = "";
    public string Url { get; init; } = "";
}

public sealed class ChangelogBullet
{
    public string Text { get; init; } = "";
    public IReadOnlyList<ChangelogLink> Links { get; init; } = Array.Empty<ChangelogLink>();
}

public sealed class ChangelogEntry
{
    public string Version { get; init; } = "";
    public string Date { get; init; } = "";
    public IReadOnlyList<ChangelogBullet> En { get; init; } = Array.Empty<ChangelogBullet>();
    public IReadOnlyList<ChangelogBullet> Zh { get; init; } = Array.Empty<ChangelogBullet>();

    public IReadOnlyList<ChangelogBullet> BulletsFor(string languageCode)
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
                    En = ToBulletArray(v["en"]),
                    Zh = ToBulletArray(v["zh"]),
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

    private static ChangelogBullet[] ToBulletArray(JToken? tok)
    {
        if (tok is not JArray arr) return Array.Empty<ChangelogBullet>();
        var result = new ChangelogBullet[arr.Count];
        for (int i = 0; i < arr.Count; i++)
            result[i] = ParseBullet(arr[i]);
        return result;
    }

    private static ChangelogBullet ParseBullet(JToken tok)
    {
        if (tok is JObject obj)
            return new ChangelogBullet
            {
                Text = obj.Value<string>("text") ?? "",
                Links = ParseLinks(obj["links"]),
            };
        return new ChangelogBullet { Text = tok.Value<string>() ?? "" };
    }

    private static ChangelogLink[] ParseLinks(JToken? tok)
    {
        if (tok is not JArray arr) return Array.Empty<ChangelogLink>();
        var result = new ChangelogLink[arr.Count];
        for (int i = 0; i < arr.Count; i++)
        {
            var o = arr[i] as JObject;
            result[i] = new ChangelogLink
            {
                Label = o?.Value<string>("label") ?? "",
                Url = o?.Value<string>("url") ?? "",
            };
        }
        return result;
    }
}
