using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;

namespace Ocelot;

public static class I18N
{
    private readonly static Dictionary<string, Dictionary<string, string>> translations = new();

    private readonly static HashSet<string> reportedMissingKeys = new();

    private static string currentLanguage = "en";

    private static string fallbackLanguage = "en";

    private static string directory = "";

    public static event Action<string, string>? OnLanguageChanged;

    public static void SetLanguage(string language)
    {
        var previousLanguage = currentLanguage;
        currentLanguage = language;

        OnLanguageChanged?.Invoke(previousLanguage, currentLanguage);
    }

    public static void SetFallbackLanguage(string language)
    {
        fallbackLanguage = language;
    }

    public static void SetDirectory(string directory)
    {
        I18N.directory = directory;
    }

    public static void AddTranslations(string language, Dictionary<string, string> newTranslations)
    {
        if (!translations.TryGetValue(language, out var existing))
        {
            translations[language] = newTranslations;
        }
        else
        {
            foreach (var kvp in newTranslations)
            {
                existing[kvp.Key] = kvp.Value;
            }
        }
    }

    public static void LoadFromFile(string language, string path)
    {
        path = Path.Combine(directory, path);

        try
        {
            if (!File.Exists(path))
            {
                Logger.Warning($"[I18n] File not found: {path}");
                return;
            }

            var json = File.ReadAllText(path);
            LoadFromJson(language, json);
        }
        catch (Exception ex)
        {
            Logger.Error($"[I18n] Failed to load file for '{language}' from '{path}': {ex.Message}");
        }
    }

    public static void LoadFromJson(string language, string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            if (node is JsonObject root)
            {
                var flat = new Dictionary<string, string>();
                FlattenJson(root, flat);

                if (!translations.TryGetValue(language, out var existing))
                {
                    translations[language] = flat;
                }
                else
                {
                    foreach (var kvp in flat)
                    {
                        existing[kvp.Key] = kvp.Value;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"[I18n] Failed to load JSON for '{language}': {ex.Message}");
        }
    }

    public static void LoadAllFromDirectory(string language, string relativeDirectory)
    {
        var fullPath = Path.Combine(directory, relativeDirectory);

        if (!Directory.Exists(fullPath))
        {
            Logger.Warning($"[I18n] Directory not found: {fullPath}");
            return;
        }

        var files = Directory.GetFiles(fullPath, "*.json", SearchOption.TopDirectoryOnly);
        foreach (var file in files)
        {
            try
            {
                var json = File.ReadAllText(file);
                LoadFromJson(language, json);
            }
            catch (Exception ex)
            {
                Logger.Error($"[I18n] Failed to load file '{file}' for '{language}': {ex.Message}");
            }
        }
    }

    public static void LoadAllFromDirectory(string language)
    {
        LoadAllFromDirectory(language, language);
    }

    private static void FlattenJson(JsonObject obj, Dictionary<string, string> result, string prefix = "")
    {
        foreach (var kvp in obj)
        {
            var key = string.IsNullOrEmpty(prefix) ? kvp.Key : $"{prefix}.{kvp.Key}";

            if (kvp.Value is JsonObject nestedObj)
            {
                FlattenJson(nestedObj, result, key);
            }
            else if (kvp.Value is JsonValue value)
            {
                result[key] = value.ToString();
            }
        }
    }

    public static string T(string key)
    {
        if (translations.TryGetValue(currentLanguage, out var currentDict) && currentDict.TryGetValue(key, out var value))
        {
            return value;
        }

        if (translations.TryGetValue(fallbackLanguage, out var fallbackDict) && fallbackDict.TryGetValue(key, out var fallbackValue))
        {
            var fallbackMessageKey = $"{currentLanguage}|{key}";
            if (reportedMissingKeys.Add(fallbackMessageKey))
            {
                Logger.Warning($"A translation for '{key}' was not found for the language '{currentLanguage}', fallback '{fallbackLanguage}' used instead.");
            }

            return fallbackValue;
        }

        var missingMessageKey = $"{currentLanguage}|{fallbackLanguage}|{key}";
        if (reportedMissingKeys.Add(missingMessageKey))
        {
            Logger.Warning($"A translation for '{key}' was not found for the language '{currentLanguage}' or fallback '{fallbackLanguage}'.");
        }

        return $"Unknown translation key: [[{key}]]";
    }

    public static string T(string key, Dictionary<string, string> replacements)
    {
        var template = T(key);

        foreach (var pair in replacements)
        {
            template = template.Replace($"{{{pair.Key}}}", pair.Value);
        }

        return template;
    }
}
