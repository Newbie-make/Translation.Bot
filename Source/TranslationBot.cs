// =================================================================================================
//  TRANSLATION BOT - SHARED CODE LIBRARY v2.0 (DATABASE MODE)
// =================================================================================================
//  - [NEW] UserProfile now stores 'Username' to act as a searchable database.
//  - [NEW] GetOrUpdateProfile: Handles the "Proactive Logging". Creates or updates users instantly.
//  - [NEW] FindUserIdByUsername: Scans the local JSON to find an ID from a name.
// =================================================================================================
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace TranslationBot
{
    public static class BotConstants
    {
        public const string DATA_FOLDER_NAME = "TranslationBotFiles";
        public const string CONFIG_FILENAME = "translation_config.json";
        public const string TEMPLATES_FILENAME = "translation_templates.json";
        public const string USER_PROFILES_FILENAME = "translation_user_profiles.json";
        public const string LOG_FILENAME = "translation_log.txt";
    }

    public class DefaultSettings
    {
        public string AutoTranslateFrom { get; set; }
        public string AutoTranslateTo { get; set; }
        public string DefaultBotPersona { get; set; }

        public DefaultSettings()
        {
            AutoTranslateFrom = "en";
            AutoTranslateTo = "pt";
            DefaultBotPersona = "en-normal";
        }
    }

    public class ApiLimits
    {
        public int Flash_RequestsPerMinute { get; set; }
        public int Flash_RequestsPerDay { get; set; }
        public int Pro_RequestsPerMinute { get; set; }
        public int Pro_RequestsPerDay { get; set; }

        public ApiLimits()
        {
            Flash_RequestsPerMinute = 1000;
            Flash_RequestsPerDay = 10000;
            Pro_RequestsPerMinute = 150;
            Pro_RequestsPerDay = 10000;
        }
    }

    public class UserProfile
    {
        // [NEW] Stored to allow reverse lookup (Name -> ID) from local DB
        public string Username { get; set; } 
        public string _UserComment { get; set; } 
        public string TargetLanguage { get; set; }
        public string SpeakingLanguage { get; set; }
        public string SpeakingStyle { get; set; }
        public string Pronouns { get; set; }

        public UserProfile()
        {
            Username = "";
            TargetLanguage = "default";
            SpeakingLanguage = "en";
            SpeakingStyle = "normal";
            Pronouns = null;
        }
    }

    public class BotConfig
    {
        public DefaultSettings DefaultSettings { get; set; }
        public ApiLimits ApiLimits { get; set; }
        public List<string> WordBlocklist { get; set; }
        public Dictionary<string, string> UserBlocklist { get; set; } 
        public List<string> InferencePriority { get; set; }
        public Dictionary<string, string> LanguageMap { get; set; }
        public Dictionary<string, Dictionary<string, string>> SettingMap { get; set; }
        public Dictionary<string, Dictionary<string, string>> StyleMap { get; set; }
        public Dictionary<string, Dictionary<string, string>> ModelMap { get; set; }
        public Dictionary<string, Dictionary<string, string>> ToneMap { get; set; }
        public Dictionary<string, Dictionary<string, string>> LanguagePronounMap { get; set; }
        public Dictionary<string, string> HelpLinks { get; set; }
        public Dictionary<string, Dictionary<string, string>> PronounNormalizationMap { get; set; }

        public BotConfig()
        {
            DefaultSettings = new DefaultSettings();
            ApiLimits = new ApiLimits();
            WordBlocklist = new List<string>();
            UserBlocklist = new Dictionary<string, string>();
            InferencePriority = new List<string>();
            LanguageMap = new Dictionary<string, string>();
            SettingMap = new Dictionary<string, Dictionary<string, string>>();
            StyleMap = new Dictionary<string, Dictionary<string, string>>();
            ModelMap = new Dictionary<string, Dictionary<string, string>>();
            ToneMap = new Dictionary<string, Dictionary<string, string>>();
            LanguagePronounMap = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            HelpLinks = new Dictionary<string, string>();
            PronounNormalizationMap = new Dictionary<string, Dictionary<string, string>>();
        }
    }

    public static class I18nHelper
    {
        private static readonly Regex SelectPattern = new Regex(@"\{(\w+),\s*select,\s*(.*)\}", RegexOptions.Singleline);

        public static string Format(string template, Dictionary<string, string> variables, object[] standardArgs)
        {
            if (string.IsNullOrEmpty(template)) return "";

            var match = SelectPattern.Match(template);
            if (match.Success)
            {
                string variableKey = match.Groups[1].Value;
                string optionsBlock = match.Groups[2].Value;
                string userValue = "other";
                
                if (variables != null && variables.ContainsKey(variableKey))
                {
                    userValue = variables[variableKey];
                }

                string selectedText = ExtractOption(optionsBlock, userValue);

                if (string.IsNullOrEmpty(selectedText))
                {
                    selectedText = ExtractOption(optionsBlock, "other");
                }

                template = template.Substring(0, match.Index) + (selectedText ?? "") + template.Substring(match.Index + match.Length);
            }

            if (standardArgs != null && standardArgs.Length > 0)
            {
                try
                {
                    template = string.Format(template, standardArgs);
                }
                catch
                {
                    return template; 
                }
            }

            return template;
        }

        private static string ExtractOption(string block, string key)
        {
            string keyPattern = key + " {";
            int keyIndex = block.IndexOf(keyPattern);
            if (keyIndex == -1) return null;

            int startIndex = keyIndex + keyPattern.Length; 
            int braceCount = 1;
            int endIndex = startIndex;

            while (endIndex < block.Length && braceCount > 0)
            {
                if (block[endIndex] == '{') braceCount++;
                else if (block[endIndex] == '}') braceCount--;
                endIndex++;
            }

            if (braceCount == 0)
            {
                return block.Substring(startIndex, endIndex - startIndex - 1);
            }
            return null;
        }
    }

    public static class BotHelpers
    {
        public static T LoadJsonFile<T>(string fileName, Action<string, string> logToFile) where T : class
        {
            try
            {
                string filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, BotConstants.DATA_FOLDER_NAME, fileName);
                if (!File.Exists(filePath))
                {
                    logToFile("CRITICAL", string.Format("{0} not found at {1}.", fileName, filePath));
                    return null;
                }
                return JsonConvert.DeserializeObject<T>(File.ReadAllText(filePath));
            }
            catch (Exception ex)
            {
                logToFile("CRITICAL", string.Format("Failed to load or parse {0}. Error: {1}", fileName, ex.Message));
                return null;
            }
        }

        public static void SaveConfigFile(BotConfig config, Action<string, string> logToFile)
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, BotConstants.DATA_FOLDER_NAME, BotConstants.CONFIG_FILENAME);
                File.WriteAllText(path, JsonConvert.SerializeObject(config, Formatting.Indented));
            }
            catch (Exception ex)
            {
                logToFile("ERROR", string.Format("Failed to save {0}: {1}", BotConstants.CONFIG_FILENAME, ex.Message));
            }
        }
        
        public static void SaveUserProfiles(Dictionary<string, UserProfile> profiles, Action<string, string> logToFile)
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, BotConstants.DATA_FOLDER_NAME, BotConstants.USER_PROFILES_FILENAME);
                File.WriteAllText(path, JsonConvert.SerializeObject(profiles, Formatting.Indented));
            }
            catch (Exception ex)
            {
                logToFile("ERROR", string.Format("Failed to save {0}: {1}", BotConstants.USER_PROFILES_FILENAME, ex.Message));
            }
        }

        public static Dictionary<string, UserProfile> LoadUserProfiles(Action<string, string> logToFile)
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, BotConstants.DATA_FOLDER_NAME, BotConstants.USER_PROFILES_FILENAME);
                if (File.Exists(path))
                {
                    var p = JsonConvert.DeserializeObject<Dictionary<string, UserProfile>>(File.ReadAllText(path));
                    return p ?? new Dictionary<string, UserProfile>();
                }
            }
            catch (Exception ex)
            {
                logToFile("ERROR", string.Format("Failed to load or parse {0}: {1}", BotConstants.USER_PROFILES_FILENAME, ex.Message));
            }
            return new Dictionary<string, UserProfile>();
        }

        // --- NEW: THE CORE "PROACTIVE LOGGING" METHOD ---
        // This replaces the old LoadUserProfile. It handles creation, username updates, and saving automatically.
        public static UserProfile GetOrUpdateProfile(string userId, string username, BotConfig config, Action<string, string> logToFile)
        {
            var profiles = LoadUserProfiles(logToFile);
            bool saveNeeded = false;
            UserProfile profile;

            if (!profiles.TryGetValue(userId, out profile))
            {
                // New user seen for the first time!
                profile = CreateDefaultUserProfile(config, logToFile);
                profile.Username = username; // Store the name for lookup later
                profiles[userId] = profile;
                saveNeeded = true;
            }
            else
            {
                // Existing user, check if name changed
                if (profile.Username != username)
                {
                    profile.Username = username;
                    saveNeeded = true;
                }
            }

            if (saveNeeded)
            {
                SaveUserProfiles(profiles, logToFile);
            }

            return profile;
        }

        // --- NEW: REVERSE LOOKUP FOR !TBLOCK ---
        public static string FindUserIdByUsername(string targetUsername, Action<string, string> logToFile)
        {
            var profiles = LoadUserProfiles(logToFile);
            // Search values for matching username (Case Insensitive)
            foreach(var kvp in profiles)
            {
                if (string.Equals(kvp.Value.Username, targetUsername, StringComparison.OrdinalIgnoreCase))
                {
                    return kvp.Key; // Return the UserID
                }
            }
            return null; // Not found in our local database
        }

        public static UserProfile CreateDefaultUserProfile(BotConfig config, Action<string, string> logToFile)
        {
            var defaultProfile = new UserProfile();
            if (config != null && config.DefaultSettings != null && !string.IsNullOrEmpty(config.DefaultSettings.DefaultBotPersona))
            {
                string[] personaParts = config.DefaultSettings.DefaultBotPersona.Split(new[] { '-' }, 2);
                string langPart = personaParts[0].ToLower();

                if (config.LanguageMap.ContainsKey(langPart))
                {
                    defaultProfile.SpeakingLanguage = langPart;
                }

                if (personaParts.Length > 1)
                {
                    string stylePart = personaParts[1].ToLower();
                    string resolvedStyle = ResolveKeyword(stylePart, langPart, config.StyleMap);

                    if (resolvedStyle == null)
                    {
                        foreach (var langEntry in config.StyleMap)
                        {
                            resolvedStyle = ResolveKeyword(stylePart, langEntry.Key, config.StyleMap);
                            if (resolvedStyle != null) break;
                        }
                    }
                    
                    if (resolvedStyle != null) defaultProfile.SpeakingStyle = resolvedStyle;
                }
            }
            return defaultProfile;
        }
        
        public static string ResolveKeyword(string keyword, string lang, Dictionary<string, Dictionary<string, string>> map) 
        { 
            keyword = keyword.ToLower(); 
            Dictionary<string, string> langMap; 
            string internalName; 
            if (map != null && map.TryGetValue(lang, out langMap) && langMap.TryGetValue(keyword, out internalName)) return internalName; 
            if (map != null && map.TryGetValue("en", out langMap) && langMap.TryGetValue(keyword, out internalName)) return internalName; 
            return null; 
        }

        public static string MapPronounToGenderKey(string pronoun)
        {
            if (string.IsNullOrEmpty(pronoun)) return "other";
            string p = pronoun.ToLower().Trim();
            if (p == "he/him") return "male";
            if (p == "she/her") return "female";
            return "other";
        }

        public static string GetBotMessage(UserProfile profile, string baseKey, Dictionary<string, Dictionary<string, string>> messageTemplates, params object[] args)
        {
            string langCode = profile.SpeakingLanguage;
            string style = profile.SpeakingStyle;
            
            string[] keysToTry = new string[] { 
                string.Format("{0}_{1}", baseKey, style), 
                string.Format("{0}_normal", baseKey), 
                baseKey 
            };

            string template = null;
            Dictionary<string, string> langTemplates;

            if (messageTemplates != null && messageTemplates.TryGetValue(langCode, out langTemplates))
            {
                foreach (string key in keysToTry) { if (langTemplates.TryGetValue(key, out template)) break; }
            }

            if (template == null && messageTemplates != null && messageTemplates.TryGetValue("en", out langTemplates))
            {
                foreach (string key in keysToTry) { if (langTemplates.TryGetValue(key, out template)) break; }
            }

            if (template == null) return string.Format("{0} (Message template not found)", baseKey);

            var icuVariables = new Dictionary<string, string>();
            string genderKey = MapPronounToGenderKey(profile.Pronouns);
            icuVariables.Add("gender", genderKey);

            return I18nHelper.Format(template, icuVariables, args);
        }
    }
}

public class CPHInline
{
    public bool Execute()
    {
        return true;
    }
}